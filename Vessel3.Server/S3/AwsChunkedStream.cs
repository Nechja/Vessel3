using System.Text;

namespace Vessel3.Server.S3;

internal sealed class AwsChunkedStream(Stream inner) : Stream
{
    private byte[] currentChunk = [];
    private int currentOffset;
    private int currentLength;
    private bool eof;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (eof) return 0;

        if (currentOffset >= currentLength)
        {
            var header = await ReadLine(ct);
            if (header is null) { eof = true; return 0; }

            var semi = header.IndexOf(';', StringComparison.Ordinal);
            var sizeHex = semi >= 0 ? header[..semi] : header;
            var size = int.Parse(sizeHex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

            if (size is 0) { eof = true; return 0; }

            if (currentChunk.Length < size) currentChunk = new byte[size];
            currentLength = size;
            currentOffset = 0;

            await inner.ReadExactlyAsync(currentChunk.AsMemory(0, size), ct);
            await ReadCrLf(ct);
        }

        var remaining = currentLength - currentOffset;
        var take = Math.Min(remaining, buffer.Length);
        currentChunk.AsSpan(currentOffset, take).CopyTo(buffer.Span);
        currentOffset += take;
        return take;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private async Task<string?> ReadLine(CancellationToken ct)
    {
        var bytes = new List<byte>(64);
        var one = new byte[1];
        while (true)
        {
            var n = await inner.ReadAsync(one.AsMemory(0, 1), ct);
            if (n is 0) return bytes.Count is 0 ? null : Encoding.ASCII.GetString([.. bytes]);
            if (one[0] is (byte)'\r')
            {
                await inner.ReadExactlyAsync(one.AsMemory(0, 1), ct);
                return one[0] is (byte)'\n'
                    ? Encoding.ASCII.GetString([.. bytes])
                    : throw new InvalidDataException("Expected LF after CR");
            }
            bytes.Add(one[0]);
        }
    }

    private async Task ReadCrLf(CancellationToken ct)
    {
        var buf = new byte[2];
        await inner.ReadExactlyAsync(buf.AsMemory(0, 2), ct);
        if (buf[0] is not (byte)'\r' || buf[1] is not (byte)'\n')
            throw new InvalidDataException("Expected CRLF after chunk data");
    }
}
