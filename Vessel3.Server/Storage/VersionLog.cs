using MemoryPack;

namespace Vessel3.Server;

// Append-only event log per bucket. Source of truth for versioning state.
// Wire format: repeating [4-byte little-endian length][MemoryPack body].
internal sealed class VersionLog(string path) : IDisposable
{
    private readonly Lock writeLock = new();
    private FileStream? writer;
    private long nextSeq;

    public void Open(long startingSeq)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        writer = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous,
        });
        nextSeq = startingSeq;
    }

    public void Dispose()
    {
        writer?.Dispose();
        writer = null;
    }

    public VersionEvent Append(string key, string versionId, string blobSha, EventKind kind, long size, string contentType)
    {
        if (writer is null) throw new InvalidOperationException("Log not opened");

        var ev = new VersionEvent(
            Seq: 0,
            At: DateTimeOffset.UtcNow,
            Key: key,
            VersionId: versionId,
            BlobSha: blobSha,
            Kind: kind,
            Size: size,
            ContentType: contentType);

        lock (writeLock)
        {
            ev = ev with { Seq = nextSeq++ };
            var body = MemoryPackSerializer.Serialize(ev);
            Span<byte> header = stackalloc byte[4];
            BitConverter.TryWriteBytes(header, body.Length);
            writer.Write(header);
            writer.Write(body);
            writer.Flush(flushToDisk: true);
        }

        return ev;
    }

    // Read entries in order; consumer filters by seq if needed.
    public IEnumerable<VersionEvent> Replay()
    {
        if (!File.Exists(path)) yield break;

        using var fs = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite,
            BufferSize = 8192,
            Options = FileOptions.SequentialScan,
        });

        var header = new byte[4];
        while (true)
        {
            var n = fs.Read(header, 0, 4);
            if (n == 0) yield break;
            if (n != 4) throw new InvalidDataException("Truncated log header");

            var len = BitConverter.ToInt32(header, 0);
            var body = new byte[len];
            fs.ReadExactly(body, 0, len);

            var ev = MemoryPackSerializer.Deserialize<VersionEvent>(body)
                ?? throw new InvalidDataException("Null event in log");
            yield return ev;
        }
    }
}
