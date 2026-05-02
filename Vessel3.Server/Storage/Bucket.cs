namespace Vessel3.Server.Storage;

internal sealed class Bucket(string name, string path) : IDisposable
{
    private readonly VersionLog log = new(Path.Combine(path, "log"));

    public string Name { get; } = name;
    public BucketIndex Index { get; } = new(Path.Combine(path, "index.db"));
    public DateTimeOffset CreatedAt { get; private set; }

    public void Open()
    {
        Index.Open();
        CreatedAt = Directory.GetCreationTimeUtc(path);

        var maxSeq = Index.MaxSeq();
        foreach (var ev in log.Replay())
        {
            if (ev.Seq <= maxSeq) continue;
            ev.ApplyTo(Index);
            maxSeq = ev.Seq;
        }

        log.Open(maxSeq + 1);
    }

    public PutEntry AppendPut(string key, PutRequest req)
    {
        if (Index.GetCurrentPut(key) is Result<PutEntry?>.Success { Value: { } old })
        {
            log.Append(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, old.VersionId)).ApplyTo(Index);
        }

        var versionId = Ulid.NewUlid().ToString();
        var ev = (PutEvent)log.Append(new PutEvent(
            0, DateTimeOffset.UtcNow, key, versionId,
            req.BlobSha, req.Md5, req.Size, req.ContentType, req.Metadata, req.Parts));
        ev.ApplyTo(Index);

        return new PutEntry(versionId, ev.At, req.BlobSha, req.Md5, req.Size, req.ContentType, req.Metadata, req.Parts);
    }

    public bool AppendHardDeleteCurrent(string key)
    {
        if (Index.GetCurrentPut(key) is not Result<PutEntry?>.Success { Value: { } old }) return false;

        log.Append(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, old.VersionId)).ApplyTo(Index);
        return true;
    }

    public bool IsEmpty() => Index.IsEmpty();

    public void Dispose()
    {
        log.Dispose();
        Index.Dispose();
    }
}
