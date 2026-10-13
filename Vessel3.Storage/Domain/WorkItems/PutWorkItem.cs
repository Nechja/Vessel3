namespace Vessel3.Storage;

internal sealed class PutWorkItem(string key, PutRequest req) : WriteWorkItem<PutEntry>
{
    private string versionId = string.Empty;

    public override string? Key => key;

    public override bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount)
    {
        if (bucket.IsSealedForDelete)
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
            VersioningStatus.Suspended when bucket.Index.LatestVersionId(key) is "null"
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
