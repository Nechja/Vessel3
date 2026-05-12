namespace Vessel3.Server.Storage;

internal enum VersioningStatus { Unversioned, Enabled, Suspended }

internal sealed class Bucket(string name, string path) : IDisposable
{
    private readonly VersionLog log = new(Path.Combine(path, "log"));
    private readonly string versioningPath = Path.Combine(path, "versioning.txt");
    private readonly Lock writeGate = new();

    public string Name { get; } = name;
    public BucketIndex Index { get; } = new(Path.Combine(path, "index.db"));
    public DateTimeOffset CreatedAt { get; private set; }
    public VersioningStatus Versioning { get; private set; }

    public void Open()
    {
        Index.Open();
        CreatedAt = Directory.GetCreationTimeUtc(path);
        Versioning = ReadVersioning();

        var maxSeq = Index.MaxSeq();
        foreach (var ev in log.Replay())
        {
            if (ev.Seq <= maxSeq) continue;
            ev.ApplyTo(Index);
            maxSeq = ev.Seq;
        }

        log.Open(maxSeq + 1);
    }

    public void SetVersioning(VersioningStatus status)
    {
        Versioning = status;
        if (status is VersioningStatus.Unversioned)
        {
            if (File.Exists(versioningPath)) File.Delete(versioningPath);
            return;
        }
        var tmp = versioningPath + ".tmp";
        File.WriteAllText(tmp, status.ToString());
        File.Move(tmp, versioningPath, overwrite: true);
    }

    private VersioningStatus ReadVersioning() =>
        File.Exists(versioningPath)
            && Enum.TryParse<VersioningStatus>(File.ReadAllText(versioningPath).Trim(), out var s)
                ? s : VersioningStatus.Unversioned;

    public PutEntry AppendPut(string key, PutRequest req)
    {
        lock (writeGate)
        {
            if (Versioning is not VersioningStatus.Enabled
                && Index.GetCurrentPut(key) is Result<PutEntry?>.Success { Value: { } old })
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
    }

    public DeleteOutcome HardDeleteVersion(string key, string versionId)
    {
        lock (writeGate)
        {
            log.Append(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, versionId)).ApplyTo(Index);
            return new DeleteOutcome(versionId, IsDeleteMarker: false, Found: true);
        }
    }

    public DeleteOutcome AppendDelete(string key)
    {
        lock (writeGate)
        {
            if (Versioning is VersioningStatus.Enabled)
            {
                var markerVersion = Ulid.NewUlid().ToString();
                log.Append(new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, markerVersion)).ApplyTo(Index);
                return new DeleteOutcome(markerVersion, IsDeleteMarker: true, Found: true);
            }

            if (Index.GetCurrentPut(key) is not Result<PutEntry?>.Success { Value: { } old })
                return new DeleteOutcome(string.Empty, IsDeleteMarker: false, Found: false);

            log.Append(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, old.VersionId)).ApplyTo(Index);
            return new DeleteOutcome(old.VersionId, IsDeleteMarker: false, Found: true);
        }
    }

    public bool IsEmpty() => Index.IsEmpty();

    public void Dispose()
    {
        log.Dispose();
        Index.Dispose();
    }
}
