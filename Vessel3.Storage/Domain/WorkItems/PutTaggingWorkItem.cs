namespace Vessel3.Storage;

internal sealed class PutTaggingWorkItem(string key, string versionId, IReadOnlyDictionary<string, string> tags) : WriteWorkItem<PutTaggingOutcome>
{
    public override string? Key => key;

    public override bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount)
    {
        if (bucket.IsSealedForDelete)
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
