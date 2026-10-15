namespace Vessel3.Storage;

internal sealed class DeleteWorkItem(string key, string? versionId, bool bypassGovernance) : WriteWorkItem<Result<DeleteOutcome>>
{
    private DeleteOutcome? outcome;

    public override string? Key => key;

    public override bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount)
    {
        eventCount = 0;
        if (bucket.IsSealedForDelete)
        {
            Result = new NoSuchBucketError(bucket.Name);
            return false;
        }

        var evaluated = versionId is null
            ? ObjectLockEvaluator.EvaluateDelete(bucket.Index, bucket.Versioning, key, bypassGovernance)
            : ObjectLockEvaluator.EvaluateHardDelete(bucket.Index, key, versionId, bypassGovernance);

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
