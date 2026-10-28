namespace Vessel3.Storage;

internal sealed class Bucket(string name, string path, IFileSync fileSync, IDurableWrite durableWrite, IBucketIndex? index, BucketWriteQueue queue, string? localNodeId = null) : IDisposable
{
    private readonly VersionLog log = new(Path.Combine(path, "log"), fileSync, localNodeId);
    private readonly BucketConfigStore configs = new(path, durableWrite);
    private readonly Lock writeGate = queue.WriteGate;

    public Bucket(string name, string path, IFileSync fileSync, IDurableWrite durableWrite, IBucketIndex? index = null, string? localNodeId = null)
        : this(name, path, fileSync, durableWrite, index, new BucketWriteQueue(), localNodeId)
    {
    }

    public string Name => name;
    public IBucketIndex Index { get; } = index ?? new BucketIndex(Path.Combine(path, "index.db"));
    public DateTimeOffset CreatedAt { get; private set; }
    public VersioningStatus Versioning => configs.Versioning;
    public ObjectLockConfig? ObjectLock => configs.ObjectLock;
    public LifecycleConfig? Lifecycle => configs.Lifecycle;
    public WebsiteConfig? Website => configs.Website;
    public BucketAccess Access => configs.Access;
    public CorsConfig? Cors => configs.Cors;
    public long FirstAvailableLogSeq => log.FirstAvailableSeq;
    public long AppliedSeq => Index.AppliedSeq();
    internal bool IsSealedForDelete { get; private set; }

    public Result<PutEntry> GetCurrentPut(string key) => Index.GetCurrentPut(key);
    public Result<PutEntry> GetVersion(string key, string versionId) => Index.GetVersion(key, versionId);
    public (IReadOnlyList<VersionListEntry> Entries, bool IsTruncated) ListCurrent(string? prefix, KeyBound? from, int limit) => Index.ListCurrent(prefix, from, limit);
    public (IReadOnlyList<AllVersionsEntry> Entries, bool IsTruncated) ListAllVersions(string? prefix, string? keyMarker, int limit) => Index.ListAllVersions(prefix, keyMarker, limit);
    public ObjectLockStatus GetObjectLockStatus(string key, string versionId) => Index.GetObjectLockStatus(key, versionId);
    public long VersionCount() => Index.VersionCount();
    public string? LatestVersionId(string key) => Index.LatestVersionId(key);

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
        CreatedAt = ReadOrPersistCreatedAt();
        configs.Load();

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

    public Result SetVersioning(VersioningStatus status) => configs.SetVersioning(status);
    public Result SetObjectLock(ObjectLockConfig cfg) => configs.SetObjectLock(cfg);
    public Result SetLifecycle(LifecycleConfig cfg) => configs.SetLifecycle(cfg);
    public Result RemoveLifecycle() => configs.RemoveLifecycle();
    public Result SetWebsite(WebsiteConfig cfg) => configs.SetWebsite(cfg);
    public Result RemoveWebsite() => configs.RemoveWebsite();
    public Result SetAccess(BucketAccess access) => configs.SetAccess(access);
    public Result SetCors(CorsConfig cfg) => configs.SetCors(cfg);
    public Result RemoveCors() => configs.RemoveCors();

    public string? GetOwner() => Index.GetOwner();

    public void SetOwner(string ownerId)
    {
        lock (writeGate)
        {
            Index.SetOwner(ownerId);
            configs.TouchUpdatedAt();
        }
    }

    public BucketConfigSnapshot GetConfigSnapshot() =>
        configs.ToSnapshot(Index.GetOwner());

    public void ApplyConfigSnapshot(BucketConfigSnapshot snapshot, bool force = false)
    {
        lock (writeGate)
        {
            var applied = configs.ApplySnapshot(snapshot, force);
            if ((applied || force) && snapshot.OwnerId is not null)
                Index.SetOwner(snapshot.OwnerId);
        }
    }

    public IReadOnlyList<VersionEvent> ReadEventsAfter(long afterSeq, int maxCount = 1000) =>
        log.ReadEventsAfter(afterSeq, maxCount);

    public IReadOnlyList<VersionEvent> ApplyReplicatedEvents(IReadOnlyList<VersionEvent> events)
    {
        if (events.Count == 0) return [];

        lock (writeGate)
        {
            if (IsSealedForDelete) return [];

            var unseen = new List<VersionEvent>(events.Count);
            var cursors = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var ev in events)
            {
                if (!string.IsNullOrEmpty(localNodeId) && string.Equals(ev.OriginNodeId, localNodeId, StringComparison.Ordinal))
                    continue;

                if (!string.IsNullOrEmpty(ev.OriginNodeId) && ev.OriginSeq > 0)
                {
                    if (!cursors.TryGetValue(ev.OriginNodeId, out var cursor))
                    {
                        cursor = Index.GetPeerCursor(ev.OriginNodeId);
                        cursors[ev.OriginNodeId] = cursor;
                    }
                    if (ev.OriginSeq <= cursor)
                        continue;
                    cursors[ev.OriginNodeId] = ev.OriginSeq;
                }

                unseen.Add(ev);
            }

            if (unseen.Count == 0) return [];

            var assigned = log.Append(unseen);
            using (var tx = Index.BeginTransaction())
            {
                foreach (var ev in assigned)
                {
                    ev.ApplyTo(Index);
                    if (ev is PutEvent put && configs.Versioning is VersioningStatus.Unversioned)
                        Index.PurgeSupersededVersionsExceptLatest(put.Key);
                    Index.MarkApplied(ev.Seq);
                }
                tx.Commit();
            }

            return assigned;
        }
    }

    public Stream OpenSnapshotStream()
    {
        lock (writeGate)
        {
            var snapshotPath = Path.Combine(path, "snapshot.db");
            var tmp = snapshotPath + ".export.tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            Index.SnapshotTo(tmp);
            File.Move(tmp, snapshotPath, overwrite: true);
            return new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
    }

    public async Task InstallSnapshot(Stream snapshotStream, BucketConfigSnapshot? config = null, CancellationToken ct = default)
    {
        var incomingPath = Path.Combine(path, $"snapshot.{Guid.NewGuid():N}.incoming");
        await using (var fs = new FileStream(incomingPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await snapshotStream.CopyToAsync(fs, ct);
            await fs.FlushAsync(ct);
        }

        lock (writeGate)
        {
            var snapshotPath = Path.Combine(path, "snapshot.db");
            File.Move(incomingPath, snapshotPath, overwrite: true);
            Index.ReplaceFromSnapshot(snapshotPath);
            log.ResetAfterSnapshot(Index.AppliedSeq() + 1);
            if (config is not null)
            {
                _ = configs.ApplySnapshot(config, force: true);
                if (config.OwnerId is not null)
                    Index.SetOwner(config.OwnerId);
            }
        }
    }

    public bool ExpireCurrentVersion(string key, string expectedCurrentVersionId, DateTimeOffset expectedAt) =>
        Execute(new ExpireCurrentVersionWorkItem(key, expectedCurrentVersionId, expectedAt));

    public bool ReapExpiredDeleteMarker(string key, string markerVersionId) =>
        Execute(new ReapExpiredDeleteMarkerWorkItem(key, markerVersionId));

    public long LogBytes() => FileBytes(Path.Combine(path, "log"));

    public BucketStats Stats() => new(
        Name,
        Index.VersionCount(),
        FileBytes(Path.Combine(path, "index.db")),
        FileBytes(Path.Combine(path, "index.db-wal")),
        LogBytes());

    private static long FileBytes(string file) => File.Exists(file) ? new FileInfo(file).Length : 0;

    public void SetCreatedAtUnixMs(long createdAtUnixMs)
    {
        if (createdAtUnixMs <= 0) return;
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(createdAtUnixMs);
        var createdAtPath = Path.Combine(path, "created-at.txt");
        _ = durableWrite.AtomicReplace(createdAtPath, createdAtUnixMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private DateTimeOffset ReadOrPersistCreatedAt()
    {
        var createdAtPath = Path.Combine(path, "created-at.txt");
        if (File.Exists(createdAtPath)
            && long.TryParse(File.ReadAllText(createdAtPath).Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ms)
            && ms > 0)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        var now = DateTimeOffset.UtcNow;
        _ = durableWrite.AtomicReplace(createdAtPath, now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        return now;
    }

    public Result<CompactionOutcome> Compact()
    {
        lock (writeGate)
        {
            if (IsSealedForDelete) return new NoSuchBucketError(Name);

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
            if (!queue.CanSeal(Index)) return false;
            IsSealedForDelete = true;
            return true;
        }
    }

    public PutEntry AppendPut(string key, PutRequest req) =>
        Execute(new PutWorkItem(key, req));

    public PutTaggingOutcome AppendPutTagging(string key, string versionId, IReadOnlyDictionary<string, string> tags) =>
        Execute(new PutTaggingWorkItem(key, versionId, tags));

    public Result<DeleteOutcome> HardDeleteVersion(string key, string versionId, bool bypassGovernance) =>
        Execute(new DeleteWorkItem(key, versionId, bypassGovernance));

    public Result<DeleteOutcome> AppendDelete(string key, bool bypassGovernance) =>
        Execute(new DeleteWorkItem(key, null, bypassGovernance));

    public IReadOnlyList<Result<DeleteOutcome>> AppendDeleteBatch(IReadOnlyList<BatchDeleteItem> items)
    {
        if (items.Count == 0) return [];
        if (items.Count == 1)
        {
            var item = items[0];
            if (string.IsNullOrEmpty(item.Key)) return [new InvalidPathError($"{Name}/{item.Key}")];
            var res = item.VersionId is null
                ? AppendDelete(item.Key, item.BypassGovernance)
                : HardDeleteVersion(item.Key, item.VersionId, item.BypassGovernance);
            return [res];
        }
        return Execute(new BatchDeleteWorkItem(items));
    }

    public Result PutRetention(string key, string versionId, Retention next, bool bypassGovernance) =>
        Execute(new PutRetentionWorkItem(key, versionId, next, bypassGovernance));

    public Result PutLegalHold(string key, string versionId, bool on) =>
        Execute(new PutLegalHoldWorkItem(key, versionId, on));

    internal void FlushDeletes(List<(int Slot, IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome)> pending, Result<DeleteOutcome>[] results) =>
        queue.FlushDeletes(log, Index, pending, results);

    private T Execute<T>(WriteWorkItem<T> item) =>
        queue.Execute(item, this, log, Index, System.Diagnostics.Stopwatch.GetTimestamp());

    public void Dispose()
    {
        lock (writeGate)
        {
            log.Dispose();
            Index.Dispose();
        }
    }
}
