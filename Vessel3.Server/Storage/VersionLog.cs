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
            if (PosixFsync.IsLinux) { writer.Flush(); PosixFsync.DataSync(writer.SafeFileHandle); }
            else writer.Flush(flushToDisk: true);
        }
        return withSeq;
    }

    public IEnumerable<VersionEvent> Replay()
    {
        if (!File.Exists(path)) yield break;

        long completeEnd;
        using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            completeEnd = FindLastNewline(probe);
        }

        if (completeEnd < new FileInfo(path).Length)
        {
            using var trunc = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            trunc.SetLength(completeEnd);
        }

        if (completeEnd is 0) yield break;

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

    private static long FindLastNewline(Stream fs)
    {
        var len = fs.Length;
        if (len is 0) return 0;
        const int chunk = 4096;
        var buf = new byte[chunk];
        var pos = len;
        while (pos > 0)
        {
            var read = (int)Math.Min(chunk, pos);
            pos -= read;
            fs.Seek(pos, SeekOrigin.Begin);
            fs.ReadExactly(buf, 0, read);
            for (var i = read - 1; i >= 0; i--)
                if (buf[i] == (byte)'\n') return pos + i + 1;
        }
        return 0;
    }
}
