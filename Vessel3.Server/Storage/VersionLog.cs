using System.Text.Json;

namespace Vessel3.Server.Storage;

// Append-only event log per bucket. Source of truth for versioning state.
// Wire format: one JSON-encoded VersionEvent per line, LF-terminated.
// `cat log` is a meaningful operation.
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

        VersionEvent ev;
        lock (writeLock)
        {
            ev = new VersionEvent(
                Seq: nextSeq++,
                At: DateTimeOffset.UtcNow,
                Key: key,
                VersionId: versionId,
                BlobSha: blobSha,
                Kind: kind,
                Size: size,
                ContentType: contentType);

            var body = JsonSerializer.SerializeToUtf8Bytes(ev, VersionEventContext.Default.VersionEvent);
            writer.Write(body);
            writer.WriteByte((byte)'\n');
            writer.Flush(flushToDisk: true);
        }
        return ev;
    }

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
        using var reader = new StreamReader(fs);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length is 0) continue;
            var ev = JsonSerializer.Deserialize(line, VersionEventContext.Default.VersionEvent)
                ?? throw new InvalidDataException("Null event in log");
            yield return ev;
        }
    }
}
