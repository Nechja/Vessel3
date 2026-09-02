using System.Text.Json;
using Vessel3.Server;
using Vessel3.Server.Lifecycle;

namespace Vessel3.Server.Storage;

internal enum VersioningStatus { Unversioned, Enabled, Suspended }

internal sealed class Bucket(string name, string path, IFileSync fileSync, IDurableWrite durableWrite) : IDisposable
{
    private readonly VersionLog log = new(Path.Combine(path, "log"), fileSync);
    private readonly string versioningPath = Path.Combine(path, "versioning.txt");
    private readonly string objectLockPath = Path.Combine(path, "object-lock.json");
    private readonly string lifecyclePath = Path.Combine(path, "lifecycle.json");
    private readonly Lock writeGate = new();
    private bool sealedForDelete;

    public string Name { get; } = name;
    public BucketIndex Index { get; } = new(Path.Combine(path, "index.db"));
    public DateTimeOffset CreatedAt { get; private set; }
    public VersioningStatus Versioning { get; private set; }
    public ObjectLockConfig? ObjectLock { get; private set; }
    public LifecycleConfig? Lifecycle { get; private set; }

    public void Open()
    {
        var indexPath = Path.Combine(path, "index.db");
        var snapshotPath = Path.Combine(path, "snapshot.db");
        if (!File.Exists(indexPath) && File.Exists(snapshotPath))
        {
            if (File.Exists(indexPath + "-wal")) File.Delete(indexPath + "-wal");
            if (File.Exists(indexPath + "-shm")) File.Delete(indexPath + "-shm");
            File.Copy(snapshotPath, indexPath);
        }

        Index.Open();
        CreatedAt = Directory.GetCreationTimeUtc(path);
        Versioning = ReadVersioning();
        ObjectLock = ReadObjectLock();
        Lifecycle = ReadLifecycle();

        var maxSeq = Index.MaxSeq();
        foreach (var ev in log.Replay())
        {
            if (ev.Seq <= maxSeq) continue;
            ev.ApplyTo(Index);
            maxSeq = ev.Seq;
        }
        Index.MarkApplied(maxSeq);

        log.Open(Math.Max(maxSeq, Index.AppliedSeq()) + 1);
    }

    public Result SetVersioning(VersioningStatus status)
    {
        if (ObjectLock is { Enabled: true }
            && status is VersioningStatus.Suspended or VersioningStatus.Unversioned)
            return new InvalidBucketStateError("cannot suspend versioning on a bucket with Object Lock enabled");

        Versioning = status;
        if (status is VersioningStatus.Unversioned)
        {
            if (File.Exists(versioningPath)) File.Delete(versioningPath);
            return Result.Ok;
        }
        return durableWrite.AtomicReplace(versioningPath, status.ToString());
    }

    public Result SetObjectLock(ObjectLockConfig cfg)
    {
        if (cfg.Enabled && Versioning is not VersioningStatus.Enabled)
            return new InvalidBucketStateError("Object Lock requires versioning to be Enabled");
        if (ObjectLock is { Enabled: true } && !cfg.Enabled)
            return new InvalidBucketStateError("Object Lock cannot be disabled once enabled");

        ObjectLock = cfg;
        return durableWrite.AtomicReplace(objectLockPath, JsonSerializer.Serialize(cfg, ObjectLockJsonContext.Default.ObjectLockConfig));
    }

    private VersioningStatus ReadVersioning() =>
        File.Exists(versioningPath)
            && Enum.TryParse<VersioningStatus>(File.ReadAllText(versioningPath).Trim(), out var s)
                ? s : VersioningStatus.Unversioned;

    private ObjectLockConfig? ReadObjectLock() =>
        File.Exists(objectLockPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(objectLockPath), ObjectLockJsonContext.Default.ObjectLockConfig)
            : null;

    public Result SetLifecycle(LifecycleConfig cfg)
    {
        Lifecycle = cfg;
        return durableWrite.AtomicReplace(lifecyclePath, JsonSerializer.Serialize(cfg, LifecycleJsonContext.Default.LifecycleConfig));
    }

    public Result RemoveLifecycle()
    {
        Lifecycle = null;
        if (File.Exists(lifecyclePath)) File.Delete(lifecyclePath);
        return Result.Ok;
    }

    private LifecycleConfig? ReadLifecycle() =>
        File.Exists(lifecyclePath)
            ? JsonSerializer.Deserialize(File.ReadAllText(lifecyclePath), LifecycleJsonContext.Default.LifecycleConfig)
            : null;

    public bool ExpireCurrentVersion(string key, string expectedCurrentVersionId, DateTimeOffset expectedAt)
    {
        lock (writeGate)
        {
            if (Index.GetCurrentPut(key) is not Result<PutEntry?>.Success { Value: { } cur }) return false;
            if (cur.VersionId != expectedCurrentVersionId) return false;
            if (cur.At != expectedAt) return false;

            var (ret, hold) = Index.GetLock(key, cur.VersionId);
            if (hold) return false;
            if (ret is not null && ret.RetainUntilDate > DateTimeOffset.UtcNow) return false;

            switch (Versioning)
            {
                case VersioningStatus.Enabled:
                {
                    var marker = new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, Ulid.NewUlid().ToString());
                    log.Append(marker).ApplyTo(Index);
                    return true;
                }
                case VersioningStatus.Suspended:
                {
                    HardDeleteEvent? hd = LatestVersionId(key) is "null"
                        ? new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, "null")
                        : null;
                    var marker = new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, "null");
                    var applied = log.Append(hd is null ? [marker] : [hd, marker]);
                    using var tx = Index.BeginTransaction();
                    foreach (var op in applied) op.ApplyTo(Index);
                    tx.Commit();
                    return true;
                }
                default:
                {
                    log.Append(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, cur.VersionId)).ApplyTo(Index);
                    return true;
                }
            }
        }
    }

    public bool ReapExpiredDeleteMarker(string key, string markerVersionId)
    {
        lock (writeGate)
        {
            var latest = Index.LatestVersionId(key);
            if (latest != markerVersionId) return false;
            if (Index.GetCurrentKind(key) is not VersionKind.DeleteMarker) return false;
            if (Index.CountVersions(key) != 1) return false;

            log.Append(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, markerVersionId)).ApplyTo(Index);
            return true;
        }
    }

    public long LogBytes()
    {
        var logPath = Path.Combine(path, "log");
        return File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
    }

    public Result<CompactionOutcome> Compact()
    {
        lock (writeGate)
        {
            if (sealedForDelete) return new NoSuchBucketError(Name);

            var before = LogBytes();
            var snapshotPath = Path.Combine(path, "snapshot.db");
            var tmp = snapshotPath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            Index.SnapshotTo(tmp);
            using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (fileSync.SyncData(fs) is Result.Failure sf) return sf.Error;
            }
            File.Move(tmp, snapshotPath, overwrite: true);
            if (fileSync.SyncDirectory(path) is Result.Failure df) return df.Error;

            log.Compact(Index.AppliedSeq());
            return new CompactionOutcome(before, LogBytes());
        }
    }

    public bool TrySealForDelete()
    {
        lock (writeGate)
        {
            if (!Index.IsEmpty()) return false;
            sealedForDelete = true;
            return true;
        }
    }

    public PutEntry AppendPut(string key, PutRequest req)
    {
        lock (writeGate)
        {
            if (sealedForDelete) throw new InvalidOperationException($"bucket {Name} is being deleted");
            var versionId = Versioning is VersioningStatus.Suspended ? "null" : Ulid.NewUlid().ToString();
            var putEvent = new PutEvent(
                0, DateTimeOffset.UtcNow, key, versionId,
                req.BlobSha, req.Md5, req.Size, req.ContentType, req.Metadata, req.Parts,
                req.Tags, req.Crc32, req.Crc32C, req.Sha1,
                RetentionMode: req.Retention?.Mode,
                RetainUntilUnixSeconds: req.Retention?.RetainUntilDate.ToUnixTimeSeconds(),
                LegalHoldOn: req.LegalHoldOn,
                SystemHeaders: req.SystemHeaders);

            HardDeleteEvent? hardDelete = Versioning switch
            {
                VersioningStatus.Unversioned when Index.GetCurrentPut(key) is Result<PutEntry?>.Success { Value: { } oldU }
                    => new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, oldU.VersionId),
                VersioningStatus.Suspended when LatestVersionId(key) is "null"
                    => new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, "null"),
                _ => null,
            };

            var applied = log.Append(hardDelete is null ? [putEvent] : [hardDelete, putEvent]);
            var assignedPut = (PutEvent)applied[^1];

            using (var tx = Index.BeginTransaction())
            {
                foreach (var op in applied) op.ApplyTo(Index);
                tx.Commit();
            }

            return new PutEntry(versionId, assignedPut.At, req.BlobSha, req.Md5, req.Size, req.ContentType, req.Metadata, req.Parts,
                req.Tags, req.Crc32, req.Crc32C, req.Sha1,
                Retention: req.Retention, LegalHoldOn: req.LegalHoldOn,
                SystemHeaders: req.SystemHeaders);
        }
    }

    public PutTaggingOutcome AppendPutTagging(string key, string versionId, IReadOnlyDictionary<string, string> tags)
    {
        lock (writeGate)
        {
            log.Append(new PutTaggingEvent(0, DateTimeOffset.UtcNow, key, versionId, tags)).ApplyTo(Index);
            return new PutTaggingOutcome(versionId);
        }
    }

    public Result<DeleteOutcome> HardDeleteVersion(string key, string versionId, bool bypassGovernance) =>
        AppendDeleteBatch([new BatchDeleteItem(key, versionId, bypassGovernance)])[0];

    public Result<DeleteOutcome> AppendDelete(string key, bool bypassGovernance) =>
        AppendDeleteBatch([new BatchDeleteItem(key, null, bypassGovernance)])[0];

    public IReadOnlyList<Result<DeleteOutcome>> AppendDeleteBatch(IReadOnlyList<BatchDeleteItem> items)
    {
        var results = new Result<DeleteOutcome>[items.Count];
        lock (writeGate)
        {
            if (sealedForDelete)
            {
                for (var i = 0; i < items.Count; i++) results[i] = new NoSuchBucketError(Name);
                return results;
            }

            var pending = new List<(int Slot, IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (string.IsNullOrEmpty(item.Key))
                {
                    results[i] = new InvalidPathError($"{Name}/{item.Key}");
                    continue;
                }
                if (!seen.Add(item.Key))
                {
                    FlushDeletes(pending, results);
                    seen.Clear();
                    seen.Add(item.Key);
                }
                var evaluated = item.VersionId is null
                    ? EvaluateDelete(item.Key, item.BypassGovernance)
                    : EvaluateHardDelete(item.Key, item.VersionId, item.BypassGovernance);
                if (evaluated.TryGetValue(out var ok, out var err)) pending.Add((i, ok.Events, ok.Outcome));
                else results[i] = err;
            }
            FlushDeletes(pending, results);
        }
        return results;
    }

    private void FlushDeletes(List<(int Slot, IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome)> pending, Result<DeleteOutcome>[] results)
    {
        var events = new List<VersionEvent>();
        foreach (var (_, evs, _) in pending) events.AddRange(evs);
        if (events.Count > 0)
        {
            var applied = log.Append(events);
            using var tx = Index.BeginTransaction();
            foreach (var op in applied) op.ApplyTo(Index);
            tx.Commit();
        }
        foreach (var (slot, _, outcome) in pending) results[slot] = outcome;
        pending.Clear();
    }

    private Result<(IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome)> EvaluateHardDelete(string key, string versionId, bool bypassGovernance)
    {
        var (ret, hold) = Index.GetLock(key, versionId);
        if (hold) return new AccessDeniedError($"legal hold on {key}@{versionId}");
        if (ret is not null && ret.RetainUntilDate > DateTimeOffset.UtcNow)
        {
            if (ret.Mode is RetentionMode.Compliance)
                return new AccessDeniedError($"COMPLIANCE retention on {key}@{versionId} until {ret.RetainUntilDate:O}");
            if (!bypassGovernance)
                return new AccessDeniedError($"GOVERNANCE retention on {key}@{versionId}; bypass header required");
        }
        IReadOnlyList<VersionEvent> events = [new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, versionId)];
        return (events, new DeleteOutcome(versionId, IsDeleteMarker: false, Found: true));
    }

    private Result<(IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome)> EvaluateDelete(string key, bool bypassGovernance)
    {
        switch (Versioning)
        {
            case VersioningStatus.Enabled:
            {
                var markerVersion = Ulid.NewUlid().ToString();
                IReadOnlyList<VersionEvent> events = [new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, markerVersion)];
                return (events, new DeleteOutcome(markerVersion, IsDeleteMarker: true, Found: true));
            }

            case VersioningStatus.Suspended:
            {
                HardDeleteEvent? hd = LatestVersionId(key) is "null"
                    ? new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, "null")
                    : null;
                var marker = new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, "null");
                IReadOnlyList<VersionEvent> events = hd is null ? [marker] : [hd, marker];
                return (events, new DeleteOutcome("null", IsDeleteMarker: true, Found: true));
            }

            case VersioningStatus.Unversioned:
            default:
            {
                if (Index.GetCurrentPut(key) is not Result<PutEntry?>.Success { Value: { } old })
                    return (Array.Empty<VersionEvent>(), new DeleteOutcome(string.Empty, IsDeleteMarker: false, Found: false));

                if (old.LegalHoldOn)
                    return new AccessDeniedError($"legal hold on {key}");
                if (old.Retention is { } r && r.RetainUntilDate > DateTimeOffset.UtcNow)
                {
                    if (r.Mode is RetentionMode.Compliance)
                        return new AccessDeniedError($"COMPLIANCE retention on {key} until {r.RetainUntilDate:O}");
                    if (!bypassGovernance)
                        return new AccessDeniedError($"GOVERNANCE retention on {key}; bypass header required");
                }

                IReadOnlyList<VersionEvent> events = [new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, old.VersionId)];
                return (events, new DeleteOutcome(old.VersionId, IsDeleteMarker: false, Found: true));
            }
        }
    }

    private string? LatestVersionId(string key) => Index.LatestVersionId(key);

    public Result PutRetention(string key, string versionId, Retention next, bool bypassGovernance)
    {
        lock (writeGate)
        {
            if (Index.GetVersion(key, versionId) is not Result<PutEntry?>.Success { Value: { } })
                return new NoSuchVersionError(key, versionId);

            var (current, _) = Index.GetLock(key, versionId);
            if (current is not null)
            {
                var lowering = next.RetainUntilDate < current.RetainUntilDate
                            || next.Mode is RetentionMode.Governance && current.Mode is RetentionMode.Compliance;
                if (current.Mode is RetentionMode.Compliance && lowering)
                    return new AccessDeniedError("COMPLIANCE retention cannot be shortened or downgraded");
                if (current.Mode is RetentionMode.Governance && lowering && !bypassGovernance)
                    return new AccessDeniedError("GOVERNANCE retention can only be shortened with the bypass header");
            }
            log.Append(new PutRetentionEvent(
                0, DateTimeOffset.UtcNow, key, versionId, next.Mode, next.RetainUntilDate.ToUnixTimeSeconds())).ApplyTo(Index);
            return Result.Ok;
        }
    }

    public Result PutLegalHold(string key, string versionId, bool on)
    {
        lock (writeGate)
        {
            if (Index.GetVersion(key, versionId) is not Result<PutEntry?>.Success { Value: { } })
                return new NoSuchVersionError(key, versionId);
            log.Append(new PutLegalHoldEvent(0, DateTimeOffset.UtcNow, key, versionId, on)).ApplyTo(Index);
            return Result.Ok;
        }
    }

    public void Dispose()
    {
        log.Dispose();
        Index.Dispose();
    }
}
