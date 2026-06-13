using System.Text.Json;
using System.Threading.Channels;

namespace Vessel3.Server.Storage;

internal sealed class VersionLog(string path) : IDisposable
{
    private readonly Lock gate = new();
    private FileStream? writer;
    private long nextSeq;
    private Channel<Envelope>? channel;
    private Task? drainer;

    private readonly record struct Envelope(byte[] Bytes, TaskCompletionSource Tcs);

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
        channel = Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        drainer = Task.Run(DrainLoop);
    }

    public void Dispose()
    {
        channel?.Writer.TryComplete();
        try { drainer?.Wait(); } catch { }
        writer?.Dispose();
        writer = null;
    }

    public VersionEvent Append(VersionEvent proto)
    {
        if (writer is null || channel is null) throw new InvalidOperationException("Log not opened");

        VersionEvent withSeq;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            withSeq = proto.WithSeq(nextSeq++);
            var body = JsonSerializer.SerializeToUtf8Bytes(withSeq, VersionEventContext.Default.VersionEvent);
            var packed = new byte[body.Length + 1];
            Buffer.BlockCopy(body, 0, packed, 0, body.Length);
            packed[^1] = (byte)'\n';
            channel.Writer.TryWrite(new Envelope(packed, tcs));
        }
        tcs.Task.GetAwaiter().GetResult();
        return withSeq;
    }

    private async Task DrainLoop()
    {
        var batch = new List<Envelope>(64);
        var reader = channel!.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (reader.TryRead(out var env)) batch.Add(env);
            try
            {
                foreach (var e in batch) writer!.Write(e.Bytes);
                writer!.Flush(flushToDisk: true);
                foreach (var e in batch) e.Tcs.SetResult();
            }
            catch (Exception ex)
            {
                foreach (var e in batch) e.Tcs.TrySetException(ex);
            }
        }
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
