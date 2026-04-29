using System.Text;

namespace Vessel3.Server.S3;

// Decodes the aws-chunked transfer encoding used when AWS SDKs send PutObject with
// x-amz-content-sha256: STREAMING-AWS4-HMAC-SHA256-PAYLOAD.
//
// Wire format (per AWS docs):
//   <hex-size>;chunk-signature=<hex>\r\n
//   <chunk-data>\r\n
//   ...
//   0;chunk-signature=<hex>\r\n
//   <optional trailers>\r\n
//
// MVP: chunk signatures are NOT verified - the request headers are already
// signed and the content hash header was part of that signature.
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

            if (size == 0) { eof = true; return 0; }

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
            if (n == 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString([.. bytes]);
            if (one[0] == (byte)'\r')
            {
                await inner.ReadExactlyAsync(one.AsMemory(0, 1), ct);
                return one[0] == (byte)'\n'
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
        if (buf[0] != (byte)'\r' || buf[1] != (byte)'\n')
            throw new InvalidDataException("Expected CRLF after chunk data");
    }
}
