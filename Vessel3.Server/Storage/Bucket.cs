namespace Vessel3.Server.Storage;

// Coordinator for one bucket: owns the open log writer and SQLite index.
// On Open, replays log into index for any gap (e.g., after a crash between
// log-append and index-update). The log is the source of truth.
internal sealed class Bucket(string name, string path, BlobPool blobs) : IDisposable
{
    private readonly VersionLog log = new(Path.Combine(path, "log"));
    private readonly BucketIndex index = new(Path.Combine(path, "index.db"));

    public string Name => name;
    public string Path => path;
    public BlobPool Blobs => blobs;
    public BucketIndex Index => index;
    public DateTimeOffset CreatedAt { get; private set; }

    public void Open()
    {
        index.Open();
        CreatedAt = Directory.GetCreationTimeUtc(path);

        var maxSeq = index.MaxSeq();
        foreach (var ev in log.Replay())
        {
            if (ev.Seq <= maxSeq) continue;
            index.Apply(ev);
            maxSeq = ev.Seq;
        }

        log.Open(maxSeq + 1);
    }

    // Disabled-mode PUT: replace the existing current version (hard-delete + put).
    // Both events go to the log so the audit trail is honest.
    public VersionEntry AppendPut(string key, string blobSha, long size, string contentType)
    {
        var current = index.GetCurrent(key);
        if (current is Result<VersionEntry?>.Success { Value: { } old })
        {
            var hd = log.Append(key, old.VersionId, string.Empty, EventKind.HardDelete, 0, string.Empty);
            index.Apply(hd);
        }

        var versionId = Ulid.NewUlid().ToString();
        var ev = log.Append(key, versionId, blobSha, EventKind.Put, size, contentType);
        index.Apply(ev);
        return new VersionEntry(versionId, blobSha, EventKind.Put, size, contentType, ev.At);
    }

    // Disabled-mode DELETE: hard-delete the current version (no-op if absent).
    // Returns whether something was actually removed.
    public bool AppendHardDeleteCurrent(string key)
    {
        var current = index.GetCurrent(key);
        if (current is not Result<VersionEntry?>.Success { Value: { } old }) return false;

        var ev = log.Append(key, old.VersionId, string.Empty, EventKind.HardDelete, 0, string.Empty);
        index.Apply(ev);
        return true;
    }

    public bool IsEmpty() => index.IsEmpty();

    public void Dispose()
    {
        log.Dispose();
        index.Dispose();
    }
}
