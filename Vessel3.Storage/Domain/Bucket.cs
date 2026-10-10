using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Vessel3.Storage;

internal sealed class Bucket(string name, string path, IFileSync fileSync, IDurableWrite durableWrite) : IDisposable
{
    private readonly VersionLog log = new(Path.Combine(path, "log"), fileSync);
    private readonly BucketConfigStore configs = new(path, durableWrite);
    private readonly Lock writeGate = new();
    private readonly Lock queueLock = new();
    private readonly Queue<WriteWorkItem> queue = new();
    private bool isDraining;
    private bool sealedForDelete;

    public string Name { get; } = name;
    public BucketIndex Index { get; } = new(Path.Combine(path, "index.db"));
    public DateTimeOffset CreatedAt { get; private set; }
    public VersioningStatus Versioning => configs.Versioning;
    public ObjectLockConfig? ObjectLock => configs.ObjectLock;
    public LifecycleConfig? Lifecycle => configs.Lifecycle;
    public WebsiteConfig? Website => configs.Website;
    public BucketAccess Access => configs.Access;
    public CorsConfig? Cors => configs.Cors;

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
        }
    }

    public bool ExpireCurrentVersion(string key, string expectedCurrentVersionId, DateTimeOffset expectedAt) =>
        Execute(new ExpireCurrentVersionWorkItem(key, expectedCurrentVersionId, expectedAt), Stopwatch.GetTimestamp());

    public bool ReapExpiredDeleteMarker(string key, string markerVersionId) =>
        Execute(new ReapExpiredDeleteMarkerWorkItem(key, markerVersionId), Stopwatch.GetTimestamp());

    public long LogBytes() => FileBytes(Path.Combine(path, "log"));

    public BucketStats Stats() => new(
        Name,
        Index.VersionCount(),
        FileBytes(Path.Combine(path, "index.db")),
        FileBytes(Path.Combine(path, "index.db-wal")),
        LogBytes());

    private static long FileBytes(string file) => File.Exists(file) ? new FileInfo(file).Length : 0;

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
            lock (queueLock)
            {
                if (queue.Count > 0 || !Index.IsEmpty()) return false;
                sealedForDelete = true;
                return true;
            }
        }
    }

    public PutEntry AppendPut(string key, PutRequest req) =>
        Execute(new PutWorkItem(key, req), Stopwatch.GetTimestamp());

    public PutTaggingOutcome AppendPutTagging(string key, string versionId, IReadOnlyDictionary<string, string> tags) =>
        Execute(new PutTaggingWorkItem(key, versionId, tags), Stopwatch.GetTimestamp());

    public Result<DeleteOutcome> HardDeleteVersion(string key, string versionId, bool bypassGovernance) =>
        Execute(new DeleteWorkItem(key, versionId, bypassGovernance), Stopwatch.GetTimestamp());

    public Result<DeleteOutcome> AppendDelete(string key, bool bypassGovernance) =>
        Execute(new DeleteWorkItem(key, null, bypassGovernance), Stopwatch.GetTimestamp());

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
        return Execute(new BatchDeleteWorkItem(items), Stopwatch.GetTimestamp());
    }

    private void FlushDeletes(List<(int Slot, IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome)> pending, Result<DeleteOutcome>[] results)
    {
        List<VersionEvent> events = [];
        foreach (var (_, evs, _) in pending) events.AddRange(evs);
        if (events.Count > 0)
        {
            var applied = log.Append(events);
            using var commit = RequestTrace.Time(Stage.IndexCommit);
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
                    return ([], new DeleteOutcome(string.Empty, IsDeleteMarker: false, Found: false));

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

    public Result PutRetention(string key, string versionId, Retention next, bool bypassGovernance) =>
        Execute(new PutRetentionWorkItem(key, versionId, next, bypassGovernance), Stopwatch.GetTimestamp());

    public Result PutLegalHold(string key, string versionId, bool on) =>
        Execute(new PutLegalHoldWorkItem(key, versionId, on), Stopwatch.GetTimestamp());

    private T Execute<T>(WriteWorkItem<T> item, long startWait)
    {
        try
        {
            bool isLeader;
            lock (queueLock)
            {
                queue.Enqueue(item);
                if (!isDraining)
                {
                    isDraining = true;
                    isLeader = true;
                }
                else
                {
                    isLeader = false;
                }
            }

            while (true)
            {
                if (isLeader)
                {
                    DrainBatch();
                    break;
                }

                item.Wait();

                if (item.PromotedToLeader)
                {
                    item.ResetSignal();
                    item.PromotedToLeader = false;
                    isLeader = true;
                    continue;
                }

                break;
            }

            RequestTrace.Since(Stage.WriteLock, startWait);
            if (item.Error is not null)
            {
                ExceptionDispatchInfo.Throw(item.Error);
            }
            return item.Result;
        }
        finally
        {
            item.Dispose();
        }
    }

    private void DrainBatch()
    {
        List<WriteWorkItem> batch;
        lock (queueLock)
        {
            if (queue.Count == 0)
            {
                isDraining = false;
                return;
            }
            batch = new List<WriteWorkItem>(queue.Count);
            while (queue.Count > 0)
            {
                batch.Add(queue.Dequeue());
            }
        }

        try
        {
            try
            {
                ProcessBatch(batch);
            }
            catch (Exception ex)
            {
                foreach (var item in batch)
                {
                    item.Complete(ex);
                }
            }
        }
        finally
        {
            lock (queueLock)
            {
                if (queue.Count > 0)
                {
                    var nextLeader = queue.Peek();
                    nextLeader.PromotedToLeader = true;
                    nextLeader.SignalPromotion();
                }
                else
                {
                    isDraining = false;
                }
            }
        }
    }

    private void ProcessBatch(List<WriteWorkItem> batch)
    {
        lock (writeGate)
        {
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            List<(WriteWorkItem Item, int EventCount)> subBatchItems = [];
            List<VersionEvent> subBatchEvents = [];

            void FlushSubBatch()
            {
                if (subBatchEvents.Count > 0)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    var applied = log.Append(subBatchEvents);
                    var logTicks = Stopwatch.GetTimestamp() - t0;

                    long indexTicks;
                    using (var commit = RequestTrace.Time(Stage.IndexCommit))
                    using (var tx = Index.BeginTransaction())
                    {
                        var t1 = Stopwatch.GetTimestamp();
                        foreach (var op in applied) op.ApplyTo(Index);
                        tx.Commit();
                        indexTicks = Stopwatch.GetTimestamp() - t1;
                    }

                    var eventIndex = 0;
                    foreach (var (item, count) in subBatchItems)
                    {
                        if (count > 0)
                        {
                            item.OnCommitted(this, applied, eventIndex, count);
                            eventIndex += count;
                        }
                        if (item.Trace is { } followerTrace && !ReferenceEquals(followerTrace, RequestTrace.Current))
                        {
                            followerTrace.Add(Stage.LogSync, logTicks);
                            followerTrace.Add(Stage.IndexCommit, indexTicks);
                        }
                        item.Complete();
                    }
                }
                else
                {
                    foreach (var (item, _) in subBatchItems)
                    {
                        item.Complete();
                    }
                }

                subBatchItems.Clear();
                subBatchEvents.Clear();
                seenKeys.Clear();
            }

            foreach (var item in batch)
            {
                if (item.Key is { } key)
                {
                    if (!seenKeys.Add(key))
                    {
                        FlushSubBatch();
                        seenKeys.Add(key);
                    }
                }
                else if (item is BatchDeleteWorkItem)
                {
                    FlushSubBatch();
                }

                if (!item.Prepare(this, subBatchEvents, out var eventCount))
                {
                    item.Complete();
                    continue;
                }

                subBatchItems.Add((item, eventCount));
            }

            FlushSubBatch();
        }
    }

    public void Dispose()
    {
        lock (writeGate)
        {
            log.Dispose();
            Index.Dispose();
        }
    }

    private abstract class WriteWorkItem : IDisposable
    {
        private readonly ManualResetEventSlim signal = new(false);
        public RequestTrace? Trace { get; } = RequestTrace.Current;
        public Exception? Error { get; private set; }
        public bool PromotedToLeader { get; set; }
        public abstract string? Key { get; }

        public void Wait() => signal.Wait();

        public void Complete(Exception? error = null)
        {
            if (error is not null) Error = error;
            signal.Set();
        }

        public void SignalPromotion() => signal.Set();
        public void ResetSignal() => signal.Reset();
        public void Dispose() => signal.Dispose();

        public abstract bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount);
        public abstract void OnCommitted(Bucket bucket, IReadOnlyList<VersionEvent> appliedEvents, int startIndex, int count);
    }

    private abstract class WriteWorkItem<T> : WriteWorkItem
    {
        public T Result { get; protected set; } = default!;
    }

    private sealed class PutWorkItem(string key, PutRequest req) : WriteWorkItem<PutEntry>
    {
        private string versionId = string.Empty;

        public override string? Key => key;

        public override bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount)
        {
            if (bucket.sealedForDelete)
            {
                Complete(new InvalidOperationException($"bucket {bucket.Name} is being deleted"));
                eventCount = 0;
                return false;
            }

            versionId = bucket.Versioning is VersioningStatus.Suspended ? "null" : Ulid.NewUlid().ToString();
            var putEvent = new PutEvent(
                0, DateTimeOffset.UtcNow, key, versionId,
                req.BlobSha, req.Md5, req.Size, req.ContentType, req.Metadata, req.Parts,
                req.Tags, req.Crc32, req.Crc32C, req.Sha1,
                RetentionMode: req.Retention?.Mode,
                RetainUntilUnixSeconds: req.Retention?.RetainUntilDate.ToUnixTimeSeconds(),
                LegalHoldOn: req.LegalHoldOn,
                SystemHeaders: req.SystemHeaders);

            HardDeleteEvent? hardDelete = bucket.Versioning switch
            {
                VersioningStatus.Unversioned when bucket.Index.GetCurrentPutVersionId(key) is { } oldVid
                    => new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, oldVid),
                VersioningStatus.Suspended when bucket.LatestVersionId(key) is "null"
                    => new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, "null"),
                _ => null,
            };

            if (hardDelete is not null)
            {
                batchEvents.Add(hardDelete);
                batchEvents.Add(putEvent);
                eventCount = 2;
            }
            else
            {
                batchEvents.Add(putEvent);
                eventCount = 1;
            }

            return true;
        }

        public override void OnCommitted(Bucket bucket, IReadOnlyList<VersionEvent> appliedEvents, int startIndex, int count)
        {
            var assignedPut = (PutEvent)appliedEvents[startIndex + count - 1];
            Result = new PutEntry(
                versionId, assignedPut.At, req.BlobSha, req.Md5, req.Size, req.ContentType, req.Metadata, req.Parts,
                req.Tags, req.Crc32, req.Crc32C, req.Sha1,
                Retention: req.Retention, LegalHoldOn: req.LegalHoldOn,
                SystemHeaders: req.SystemHeaders);
        }
    }

    private sealed class PutTaggingWorkItem(string key, string versionId, IReadOnlyDictionary<string, string> tags) : WriteWorkItem<PutTaggingOutcome>
    {
        public override string? Key => key;

        public override bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount)
        {
            if (bucket.sealedForDelete)
            {
                Complete(new InvalidOperationException($"bucket {bucket.Name} is being deleted"));
                eventCount = 0;
                return false;
            }

            batchEvents.Add(new PutTaggingEvent(0, DateTimeOffset.UtcNow, key, versionId, tags));
            eventCount = 1;
            return true;
        }

        public override void OnCommitted(Bucket bucket, IReadOnlyList<VersionEvent> appliedEvents, int startIndex, int count)
        {
            Result = new PutTaggingOutcome(versionId);
        }
    }

    private sealed class DeleteWorkItem(string key, string? versionId, bool bypassGovernance) : WriteWorkItem<Result<DeleteOutcome>>
    {
        private DeleteOutcome? outcome;

        public override string? Key => key;

        public override bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount)
        {
            eventCount = 0;
            if (bucket.sealedForDelete)
            {
                Result = new NoSuchBucketError(bucket.Name);
                return false;
            }

            var evaluated = versionId is null
                ? bucket.EvaluateDelete(key, bypassGovernance)
                : bucket.EvaluateHardDelete(key, versionId, bypassGovernance);

            if (!evaluated.TryGetValue(out var ok, out var err))
            {
                Result = err;
                return false;
            }

            outcome = ok.Outcome;
            batchEvents.AddRange(ok.Events);
            eventCount = ok.Events.Count;
            if (eventCount == 0)
            {
                Result = ok.Outcome;
                return false;
            }

            return true;
        }

        public override void OnCommitted(Bucket bucket, IReadOnlyList<VersionEvent> appliedEvents, int startIndex, int count)
        {
            Result = outcome!;
        }
    }

    private sealed class BatchDeleteWorkItem(IReadOnlyList<BatchDeleteItem> items) : WriteWorkItem<IReadOnlyList<Result<DeleteOutcome>>>
    {
        public override string? Key => null;

        public override bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount)
        {
            eventCount = 0;
            if (bucket.sealedForDelete)
            {
                var r = new Result<DeleteOutcome>[items.Count];
                for (var i = 0; i < items.Count; i++) r[i] = new NoSuchBucketError(bucket.Name);
                Result = r;
                return false;
            }

            var results = new Result<DeleteOutcome>[items.Count];
            List<(int Slot, IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome)> pending = [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (string.IsNullOrEmpty(item.Key))
                {
                    results[i] = new InvalidPathError($"{bucket.Name}/{item.Key}");
                    continue;
                }
                if (!seen.Add(item.Key))
                {
                    bucket.FlushDeletes(pending, results);
                    seen.Clear();
                    seen.Add(item.Key);
                }
                var evaluated = item.VersionId is null
                    ? bucket.EvaluateDelete(item.Key, item.BypassGovernance)
                    : bucket.EvaluateHardDelete(item.Key, item.VersionId, item.BypassGovernance);
                if (evaluated.TryGetValue(out var ok, out var err)) pending.Add((i, ok.Events, ok.Outcome));
                else results[i] = err;
            }
            bucket.FlushDeletes(pending, results);
            Result = results;
            return false;
        }

        public override void OnCommitted(Bucket bucket, IReadOnlyList<VersionEvent> appliedEvents, int startIndex, int count)
        {
        }
    }

    private sealed class ExpireCurrentVersionWorkItem(string key, string expectedCurrentVersionId, DateTimeOffset expectedAt) : WriteWorkItem<bool>
    {
        public override string? Key => key;

        public override bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount)
        {
            eventCount = 0;
            if (bucket.sealedForDelete)
            {
                Result = false;
                return false;
            }

            if (bucket.Index.GetCurrentPut(key) is not Result<PutEntry?>.Success { Value: { } cur }
                || cur.VersionId != expectedCurrentVersionId
                || cur.At != expectedAt)
            {
                Result = false;
                return false;
            }

            var (ret, hold) = bucket.Index.GetLock(key, cur.VersionId);
            if (hold || (ret is not null && ret.RetainUntilDate > DateTimeOffset.UtcNow))
            {
                Result = false;
                return false;
            }

            switch (bucket.Versioning)
            {
                case VersioningStatus.Enabled:
                    batchEvents.Add(new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, Ulid.NewUlid().ToString()));
                    eventCount = 1;
                    break;
                case VersioningStatus.Suspended:
                    if (bucket.LatestVersionId(key) is "null")
                    {
                        batchEvents.Add(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, "null"));
                        batchEvents.Add(new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, "null"));
                        eventCount = 2;
                    }
                    else
                    {
                        batchEvents.Add(new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, "null"));
                        eventCount = 1;
                    }
                    break;
                default:
                    batchEvents.Add(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, cur.VersionId));
                    eventCount = 1;
                    break;
            }

            Result = true;
            return true;
        }

        public override void OnCommitted(Bucket bucket, IReadOnlyList<VersionEvent> appliedEvents, int startIndex, int count)
        {
            Result = true;
        }
    }

    private sealed class ReapExpiredDeleteMarkerWorkItem(string key, string markerVersionId) : WriteWorkItem<bool>
    {
        public override string? Key => key;

        public override bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount)
        {
            eventCount = 0;
            if (bucket.sealedForDelete)
            {
                Result = false;
                return false;
            }

            var latest = bucket.Index.LatestVersionId(key);
            if (latest != markerVersionId
                || bucket.Index.GetCurrentKind(key) is not VersionKind.DeleteMarker
                || bucket.Index.CountVersions(key) != 1)
            {
                Result = false;
                return false;
            }

            batchEvents.Add(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, markerVersionId));
            eventCount = 1;
            Result = true;
            return true;
        }

        public override void OnCommitted(Bucket bucket, IReadOnlyList<VersionEvent> appliedEvents, int startIndex, int count)
        {
            Result = true;
        }
    }

    private sealed class PutRetentionWorkItem(string key, string versionId, Retention next, bool bypassGovernance) : WriteWorkItem<Result>
    {
        public override string? Key => key;

        public override bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount)
        {
            eventCount = 0;
            if (bucket.sealedForDelete)
            {
                Result = new NoSuchBucketError(bucket.Name);
                return false;
            }

            if (bucket.Index.GetVersion(key, versionId) is not Result<PutEntry?>.Success { Value: { } })
            {
                Result = new NoSuchVersionError(key, versionId);
                return false;
            }

            var (current, _) = bucket.Index.GetLock(key, versionId);
            if (current is not null)
            {
                var lowering = next.RetainUntilDate < current.RetainUntilDate
                            || next.Mode is RetentionMode.Governance && current.Mode is RetentionMode.Compliance;
                if (current.Mode is RetentionMode.Compliance && lowering)
                {
                    Result = new AccessDeniedError("COMPLIANCE retention cannot be shortened or downgraded");
                    return false;
                }
                if (current.Mode is RetentionMode.Governance && lowering && !bypassGovernance)
                {
                    Result = new AccessDeniedError("GOVERNANCE retention can only be shortened with the bypass header");
                    return false;
                }
            }

            batchEvents.Add(new PutRetentionEvent(
                0, DateTimeOffset.UtcNow, key, versionId, next.Mode, next.RetainUntilDate.ToUnixTimeSeconds()));
            eventCount = 1;
            Result = Result.Ok;
            return true;
        }

        public override void OnCommitted(Bucket bucket, IReadOnlyList<VersionEvent> appliedEvents, int startIndex, int count)
        {
            Result = Result.Ok;
        }
    }

    private sealed class PutLegalHoldWorkItem(string key, string versionId, bool on) : WriteWorkItem<Result>
    {
        public override string? Key => key;

        public override bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount)
        {
            eventCount = 0;
            if (bucket.sealedForDelete)
            {
                Result = new NoSuchBucketError(bucket.Name);
                return false;
            }

            if (bucket.Index.GetVersion(key, versionId) is not Result<PutEntry?>.Success { Value: { } })
            {
                Result = new NoSuchVersionError(key, versionId);
                return false;
            }

            batchEvents.Add(new PutLegalHoldEvent(0, DateTimeOffset.UtcNow, key, versionId, on));
            eventCount = 1;
            Result = Result.Ok;
            return true;
        }

        public override void OnCommitted(Bucket bucket, IReadOnlyList<VersionEvent> appliedEvents, int startIndex, int count)
        {
            Result = Result.Ok;
        }
    }
}
