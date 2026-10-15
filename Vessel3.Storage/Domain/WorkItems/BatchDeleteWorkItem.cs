namespace Vessel3.Storage;

internal sealed class BatchDeleteWorkItem(IReadOnlyList<BatchDeleteItem> items) : WriteWorkItem<IReadOnlyList<Result<DeleteOutcome>>>
{
    public override string? Key => null;

    public override bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount)
    {
        eventCount = 0;
        if (bucket.IsSealedForDelete)
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
                ? ObjectLockEvaluator.EvaluateDelete(bucket.Index, bucket.Versioning, item.Key, item.BypassGovernance)
                : ObjectLockEvaluator.EvaluateHardDelete(bucket.Index, item.Key, item.VersionId, item.BypassGovernance);
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
