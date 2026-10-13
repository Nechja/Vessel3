namespace Vessel3.Storage;

internal sealed class PutRetentionWorkItem(string key, string versionId, Retention next, bool bypassGovernance) : WriteWorkItem<Result>
{
    public override string? Key => key;

    public override bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount)
    {
        eventCount = 0;
        if (bucket.IsSealedForDelete)
        {
            Result = new NoSuchBucketError(bucket.Name);
            return false;
        }

        if (!bucket.Index.GetVersion(key, versionId).TryGetValue(out _, out var err))
        {
            Result = err;
            return false;
        }

        var (current, _) = bucket.Index.GetObjectLockStatus(key, versionId);
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
