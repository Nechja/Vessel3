namespace Vessel3.Server.Storage;

// Coordinator for one bucket: owns the open log writer and SQLite index.
// On Open, replays log into index for any gap (e.g., after a crash between
// log-append and index-update). The log is the source of truth.
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
            Index.Apply(ev);
            maxSeq = ev.Seq;
        }

        log.Open(maxSeq + 1);
    }

    public VersionEntry AppendPut(string key, string blobSha, long size, string contentType)
    {
        var current = Index.GetCurrent(key);
        if (current is Result<VersionEntry?>.Success { Value: { } old })
        {
            var hd = log.Append(key, old.VersionId, string.Empty, EventKind.HardDelete, 0, string.Empty);
            Index.Apply(hd);
        }

        var versionId = Ulid.NewUlid().ToString();
        var ev = log.Append(key, versionId, blobSha, EventKind.Put, size, contentType);
        Index.Apply(ev);
        return new VersionEntry(versionId, blobSha, EventKind.Put, size, contentType, ev.At);
    }

    public bool AppendHardDeleteCurrent(string key)
    {
        var current = Index.GetCurrent(key);
        if (current is not Result<VersionEntry?>.Success { Value: { } old }) return false;

        var ev = log.Append(key, old.VersionId, string.Empty, EventKind.HardDelete, 0, string.Empty);
        Index.Apply(ev);
        return true;
    }

    public bool IsEmpty() => Index.IsEmpty();

    public void Dispose()
    {
        log.Dispose();
        Index.Dispose();
    }
}
