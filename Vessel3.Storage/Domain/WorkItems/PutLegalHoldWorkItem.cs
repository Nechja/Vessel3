namespace Vessel3.Storage;

internal sealed class PutLegalHoldWorkItem(string key, string versionId, bool on) : WriteWorkItem<Result>
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
