namespace Vessel3.Storage;

internal sealed class ReapExpiredDeleteMarkerWorkItem(string key, string markerVersionId) : WriteWorkItem<bool>
{
    public override string? Key => key;

    public override bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount)
    {
        eventCount = 0;
        if (bucket.IsSealedForDelete)
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
