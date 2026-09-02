using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Vessel3.Server.Storage;

internal sealed class VersionLog(string path, IFileSync fileSync) : IDisposable
{
    private const int HeaderLength = 21;
    private const long ResyncScanLimit = 8L * 1024 * 1024;

    private readonly Lock writeLock = new();
    private FileStream? writer;
    private long nextSeq;
    private uint previousCrc;
    private bool faulted;
    private bool chainKnown;
    private long scannedNextSeq;

    public void Open(long startingSeq)
    {
        var dir = Path.GetDirectoryName(path)!;
        if (fileSync.CreateDirectoryDurable(dir) is Result.Failure df) throw new IOException(df.Error.Message);
        var isNewFile = !File.Exists(path);
        writer = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous,
        });
        if (isNewFile && fileSync.SyncDirectory(dir) is Result.Failure sf) throw new IOException(sf.Error.Message);
        if (!chainKnown) RestoreChainState();
        nextSeq = scannedNextSeq > 0 ? scannedNextSeq : startingSeq;
    }

    public void Dispose()
    {
        writer?.Dispose();
        writer = null;
    }

    public IReadOnlyList<VersionEvent> Append(IReadOnlyList<VersionEvent> ops)
    {
        if (writer is null) throw new InvalidOperationException("Log not opened");
        if (ops.Count is 0) return [];

        lock (writeLock)
        {
            if (faulted) throw new IOException("Log is faulted; restart required");

            var seq = nextSeq;
            var assigned = new List<VersionEvent>(ops.Count);
            foreach (var op in ops) assigned.Add(op.WithSeq(seq++));

            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new LogRecord(nextSeq, DateTimeOffset.UtcNow, assigned), VersionEventContext.Default.LogRecord);
            var crc = ChainCrc(previousCrc, payload);

            try
            {
                writer.Write(Frame(payload, crc));
                if (fileSync.SyncData(writer) is Result.Failure f) throw new IOException(f.Error.Message);
            }
            catch
            {
                faulted = true;
                throw;
            }

            previousCrc = crc;
            nextSeq = seq;
            return assigned;
        }
    }

    public VersionEvent Append(VersionEvent proto) => Append([proto])[0];

    public void Compact(long throughSeq)
    {
        lock (writeLock)
        {
            if (writer is null) throw new InvalidOperationException("Log not opened");
            if (faulted) throw new IOException("Log is faulted; restart required");

            writer.Dispose();
            writer = null;

            try
            {
                var kept = new List<VersionEvent>();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    foreach (var (ev, _) in ReadRecords(fs))
                        if (ev.Seq > throughSeq) kept.Add(ev);
                }

                var tmp = path + ".tmp";
                uint chain = 0;
                using (var outStream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (kept.Count > 0)
                    {
                        var payload = JsonSerializer.SerializeToUtf8Bytes(
                            new LogRecord(kept[0].Seq, DateTimeOffset.UtcNow, kept), VersionEventContext.Default.LogRecord);
                        chain = ChainCrc(0, payload);
                        outStream.Write(Frame(payload, chain));
                    }
                    if (fileSync.SyncData(outStream) is Result.Failure sf) throw new IOException(sf.Error.Message);
                }
                File.Move(tmp, path, overwrite: true);
                if (fileSync.SyncDirectory(Path.GetDirectoryName(path)!) is Result.Failure df) throw new IOException(df.Error.Message);

                previousCrc = chain;
            }
            catch
            {
                faulted = true;
                throw;
            }

            writer = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous,
            });
        }
    }

    private static byte[] Frame(byte[] payload, uint crc)
    {
        var header = Encoding.ASCII.GetBytes($"v1 {payload.Length:x8} {crc:x8} ");
        var frame = new byte[header.Length + payload.Length + 1];
        header.CopyTo(frame, 0);
        payload.CopyTo(frame, header.Length);
        frame[^1] = (byte)'\n';
        return frame;
    }

    private static uint ChainCrc(uint previous, ReadOnlySpan<byte> payload)
    {
        Span<byte> seed = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seed, previous);
        var crc = new Crc32C();
        crc.Append(seed);
        crc.Append(payload);
        return crc.GetCurrentHashAndReset();
    }

    public IEnumerable<VersionEvent> Replay()
    {
        if (!File.Exists(path)) yield break;

        long goodEnd = 0;
        chainKnown = true;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            foreach (var (ev, end) in ReadRecords(fs))
            {
                goodEnd = end;
                yield return ev;
            }
        }

        if (goodEnd < new FileInfo(path).Length) TruncateTo(goodEnd);
    }

    private void RestoreChainState()
    {
        previousCrc = 0;
        chainKnown = true;
        if (!File.Exists(path)) return;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            foreach (var _ in ReadRecords(fs)) { }
        }
        catch (InvalidDataException)
        {
        }
    }

    private void TruncateTo(long length)
    {
        using var trunc = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        trunc.SetLength(length);
        trunc.Flush(flushToDisk: true);
    }

    private IEnumerable<(VersionEvent Event, long End)> ReadRecords(FileStream fs)
    {
        previousCrc = 0;
        scannedNextSeq = 0;
        var expectedSeq = -1L;

        while (true)
        {
            var start = fs.Position;
            if (start >= fs.Length) yield break;

            if (!TryReadRecord(fs, out var ops, out var recordSeq, out var crc, out var framed))
            {
                if (HasParsableRecordAfter(fs, start))
                    throw new InvalidDataException($"Version log corrupt at offset {start}");
                yield break;
            }

            if (expectedSeq >= 0 && recordSeq != expectedSeq)
            {
                if (HasParsableRecordAfter(fs, start))
                    throw new InvalidDataException($"Version log seq gap at offset {start}: expected {expectedSeq}, found {recordSeq}");
                yield break;
            }

            if (framed) previousCrc = crc;
            expectedSeq = recordSeq + ops.Count;
            scannedNextSeq = expectedSeq;
            var end = fs.Position;
            foreach (var op in ops) yield return (op, end);
        }
    }

    private bool TryReadRecord(FileStream fs, out IReadOnlyList<VersionEvent> ops, out long recordSeq, out uint crc, out bool framed)
    {
        ops = [];
        recordSeq = 0;
        crc = 0;
        framed = false;

        var start = fs.Position;
        var first = fs.ReadByte();
        if (first < 0) return false;
        fs.Position = start;

        if (first == '{') return TryReadLegacyRecord(fs, out ops, out recordSeq);

        var header = new byte[HeaderLength];
        if (!ReadFully(fs, header) || !ParseHeader(header, start, fs.Length, out var length, out var declaredCrc))
            return false;

        var payload = new byte[length];
        if (!ReadFully(fs, payload) || fs.ReadByte() != '\n') return false;
        if (ChainCrc(previousCrc, payload) != declaredCrc) return false;

        LogRecord? record;
        try
        {
            record = JsonSerializer.Deserialize(payload, VersionEventContext.Default.LogRecord);
        }
        catch (JsonException)
        {
            return false;
        }
        if (record is null || record.Ops.Count is 0) return false;

        ops = record.Ops;
        recordSeq = record.Seq;
        crc = declaredCrc;
        framed = true;
        return true;
    }

    private static bool TryReadLegacyRecord(FileStream fs, out IReadOnlyList<VersionEvent> ops, out long recordSeq)
    {
        ops = [];
        recordSeq = 0;
        var line = new List<byte>(512);
        while (true)
        {
            var b = fs.ReadByte();
            if (b < 0) return false;
            if (b == '\n') break;
            line.Add((byte)b);
        }

        try
        {
            var ev = JsonSerializer.Deserialize(line.ToArray(), VersionEventContext.Default.VersionEvent);
            if (ev is null) return false;
            ops = [ev];
            recordSeq = ev.Seq;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ParseHeader(ReadOnlySpan<byte> header, long start, long fileLength, out int length, out uint crc)
    {
        length = 0;
        crc = 0;
        var text = Encoding.ASCII.GetString(header);
        return text.StartsWith("v1 ", StringComparison.Ordinal)
            && text[11] == ' '
            && text[20] == ' '
            && int.TryParse(text.AsSpan(3, 8), NumberStyles.HexNumber, null, out length)
            && length >= 0
            && uint.TryParse(text.AsSpan(12, 8), NumberStyles.HexNumber, null, out crc)
            && start + HeaderLength + length + 1 <= fileLength;
    }

    private static bool ReadFully(FileStream fs, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = fs.Read(buffer[read..]);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static bool HasParsableRecordAfter(FileStream fs, long start)
    {
        var limit = Math.Min(fs.Length, start + ResyncScanLimit);
        for (var candidate = NextLineStart(fs, start, limit); candidate >= 0; candidate = NextLineStart(fs, candidate, limit))
        {
            if (IsRecordStart(fs, candidate))
            {
                fs.Position = start;
                return true;
            }
        }
        fs.Position = start;
        return false;
    }

    private static bool IsRecordStart(FileStream fs, long offset)
    {
        fs.Position = offset;
        var first = fs.ReadByte();
        if (first < 0) return false;
        fs.Position = offset;

        var header = new byte[HeaderLength];
        return first == '{'
            ? TryReadLegacyRecord(fs, out _, out _)
            : ReadFully(fs, header) && ParseHeader(header, offset, fs.Length, out _, out _);
    }

    private static long NextLineStart(FileStream fs, long from, long limit)
    {
        fs.Position = from;
        var buffer = new byte[8192];
        var position = from;
        while (position < limit)
        {
            fs.Position = position;
            var n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - position));
            if (n <= 0) return -1;
            for (var i = 0; i < n; i++)
                if (buffer[i] == (byte)'\n' && position + i + 1 < limit) return position + i + 1;
            position += n;
        }
        return -1;
    }
}
