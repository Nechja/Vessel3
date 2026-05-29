using System.Text.Json;
using Vessel3.Server;

namespace Vessel3.Server.Storage;

internal enum VersioningStatus { Unversioned, Enabled, Suspended }

internal sealed class Bucket(string name, string path) : IDisposable
{
    private readonly VersionLog log = new(Path.Combine(path, "log"));
    private readonly string versioningPath = Path.Combine(path, "versioning.txt");
    private readonly string objectLockPath = Path.Combine(path, "object-lock.json");
    private readonly string lifecyclePath = Path.Combine(path, "lifecycle.json");
    private readonly Lock writeGate = new();

    public string Name { get; } = name;
    public BucketIndex Index { get; } = new(Path.Combine(path, "index.db"));
    public DateTimeOffset CreatedAt { get; private set; }
    public VersioningStatus Versioning { get; private set; }
    public ObjectLockConfig? ObjectLock { get; private set; }
    public LifecycleConfig? Lifecycle { get; private set; }

    public void Open()
    {
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

        log.Open(maxSeq + 1);
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
        DurableWrite.AtomicReplace(versioningPath, status.ToString());
        return Result.Ok;
    }

    public Result SetObjectLock(ObjectLockConfig cfg)
    {
        if (cfg.Enabled && Versioning is not VersioningStatus.Enabled)
            return new InvalidBucketStateError("Object Lock requires versioning to be Enabled");
        if (ObjectLock is { Enabled: true } && !cfg.Enabled)
            return new InvalidBucketStateError("Object Lock cannot be disabled once enabled");

        ObjectLock = cfg;
        DurableWrite.AtomicReplace(objectLockPath, JsonSerializer.Serialize(cfg, ObjectLockJsonContext.Default.ObjectLockConfig));
        return Result.Ok;
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
        DurableWrite.AtomicReplace(lifecyclePath, JsonSerializer.Serialize(cfg, LifecycleJsonContext.Default.LifecycleConfig));
        return Result.Ok;
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
                    var assignedHd = hd is null ? null : (HardDeleteEvent)log.Append(hd);
                    var assignedMarker = (DeleteMarkerEvent)log.Append(marker);
                    using var tx = Index.BeginTransaction();
                    assignedHd?.ApplyTo(Index);
                    assignedMarker.ApplyTo(Index);
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
            if (Index.GetCurrentKind(key) is not BucketIndex.KindDeleteMarker) return false;
            if (Index.CountVersions(key) != 1) return false;

            log.Append(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, markerVersionId)).ApplyTo(Index);
            return true;
        }
    }

    public PutEntry AppendPut(string key, PutRequest req)
    {
        lock (writeGate)
        {
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

            var assignedPut = (PutEvent)log.Append(putEvent);
            HardDeleteEvent? assignedHd = hardDelete is null ? null : (HardDeleteEvent)log.Append(hardDelete);

            using (var tx = Index.BeginTransaction())
            {
                assignedHd?.ApplyTo(Index);
                assignedPut.ApplyTo(Index);
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

    public Result<DeleteOutcome> HardDeleteVersion(string key, string versionId, bool bypassGovernance)
    {
        lock (writeGate)
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
            log.Append(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, versionId)).ApplyTo(Index);
            return new DeleteOutcome(versionId, IsDeleteMarker: false, Found: true);
        }
    }

    public Result<DeleteOutcome> AppendDelete(string key, bool bypassGovernance)
    {
        lock (writeGate)
        {
            switch (Versioning)
            {
                case VersioningStatus.Enabled:
                {
                    var markerVersion = Ulid.NewUlid().ToString();
                    log.Append(new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, markerVersion)).ApplyTo(Index);
                    return new DeleteOutcome(markerVersion, IsDeleteMarker: true, Found: true);
                }

                case VersioningStatus.Suspended:
                {
                    HardDeleteEvent? hd = LatestVersionId(key) is "null"
                        ? new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, "null")
                        : null;
                    var marker = new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, "null");
                    var assignedHd = hd is null ? null : (HardDeleteEvent)log.Append(hd);
                    var assignedMarker = (DeleteMarkerEvent)log.Append(marker);
                    using (var tx = Index.BeginTransaction())
                    {
                        assignedHd?.ApplyTo(Index);
                        assignedMarker.ApplyTo(Index);
                        tx.Commit();
                    }
                    return new DeleteOutcome("null", IsDeleteMarker: true, Found: true);
                }

                case VersioningStatus.Unversioned:
                default:
                {
                    if (Index.GetCurrentPut(key) is not Result<PutEntry?>.Success { Value: { } old })
                        return new DeleteOutcome(string.Empty, IsDeleteMarker: false, Found: false);

                    if (old.LegalHoldOn)
                        return new AccessDeniedError($"legal hold on {key}");
                    if (old.Retention is { } r && r.RetainUntilDate > DateTimeOffset.UtcNow)
                    {
                        if (r.Mode is RetentionMode.Compliance)
                            return new AccessDeniedError($"COMPLIANCE retention on {key} until {r.RetainUntilDate:O}");
                        if (!bypassGovernance)
                            return new AccessDeniedError($"GOVERNANCE retention on {key}; bypass header required");
                    }

                    log.Append(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, old.VersionId)).ApplyTo(Index);
                    return new DeleteOutcome(old.VersionId, IsDeleteMarker: false, Found: true);
                }
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

    public bool IsEmpty() => Index.IsEmpty();

    public void Dispose()
    {
        log.Dispose();
        Index.Dispose();
    }
}
