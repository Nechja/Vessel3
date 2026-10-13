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

        if (!bucket.Index.GetCurrentPut(key).TryGetValue(out var cur, out _)
            || cur.VersionId != expectedCurrentVersionId
            || cur.At != expectedAt)
        {
            Result = false;
            return false;
        }

        var (ret, hold) = bucket.Index.GetObjectLockStatus(key, cur.VersionId);
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
                if (bucket.Index.LatestVersionId(key) is "null")
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
