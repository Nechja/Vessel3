using System.Text.Json;

namespace Vessel3.Server.Storage;

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

    public VersionEvent Append(VersionEvent proto)
    {
        if (writer is null) throw new InvalidOperationException("Log not opened");

        VersionEvent withSeq;
        lock (writeLock)
        {
            withSeq = proto.WithSeq(nextSeq++);
            var body = JsonSerializer.SerializeToUtf8Bytes(withSeq, VersionEventContext.Default.VersionEvent);
            writer.Write(body);
            writer.WriteByte((byte)'\n');
            writer.Flush(flushToDisk: true);
        }
        return withSeq;
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
