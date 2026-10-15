namespace Vessel3.Storage;

internal sealed class ExpireCurrentVersionWorkItem(string key, string expectedCurrentVersionId, DateTimeOffset expectedAt) : WriteWorkItem<bool>
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

        if (!bucket.Index.GetCurrentPut(key).TryGetValue(out var currentPut, out _)
            || currentPut.VersionId != expectedCurrentVersionId
            || currentPut.At != expectedAt)
        {
            Result = false;
            return false;
        }

        var (retention, hasLegalHold) = bucket.Index.GetObjectLockStatus(key, currentPut.VersionId);
        if (hasLegalHold || (retention is not null && retention.RetainUntilDate > DateTimeOffset.UtcNow))
        {
            Result = false;
            return false;
        }

        eventCount = AppendExpirationEvents(bucket, batchEvents, currentPut.VersionId);
        Result = true;
        return true;
    }

    private int AppendExpirationEvents(Bucket bucket, List<VersionEvent> batchEvents, string currentVersionId) =>
        bucket.Versioning switch
        {
            VersioningStatus.Enabled => AppendEnabledEvents(batchEvents),
            VersioningStatus.Suspended => AppendSuspendedEvents(bucket, batchEvents),
            _ => AppendUnversionedEvents(batchEvents, currentVersionId),
        };

    private int AppendEnabledEvents(List<VersionEvent> batchEvents)
    {
        batchEvents.Add(new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, Ulid.NewUlid().ToString()));
        return 1;
    }

    private int AppendSuspendedEvents(Bucket bucket, List<VersionEvent> batchEvents)
    {
        if (bucket.Index.LatestVersionId(key) is "null")
        {
            batchEvents.Add(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, "null"));
            batchEvents.Add(new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, "null"));
            return 2;
        }

        batchEvents.Add(new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, "null"));
        return 1;
    }

    private int AppendUnversionedEvents(List<VersionEvent> batchEvents, string currentVersionId)
    {
        batchEvents.Add(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, currentVersionId));
        return 1;
    }

    public override void OnCommitted(Bucket bucket, IReadOnlyList<VersionEvent> appliedEvents, int startIndex, int count)
    {
        Result = true;
    }
}
