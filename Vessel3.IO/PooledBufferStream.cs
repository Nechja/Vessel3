using System.Buffers;

namespace Vessel3.IO;

internal sealed class PooledBufferStream : Stream
{
    private readonly List<byte[]> segments = [];
    private int currentSegmentIndex = -1;
    private int currentSegmentOffset;
    private long totalBytesWritten;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => totalBytesWritten;

    public override long Position
    {
        get => totalBytesWritten;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var remaining = buffer;
        while (!remaining.IsEmpty)
        {
            if (currentSegmentIndex < 0 || currentSegmentOffset == SohBuffer.BlockSize)
            {
                segments.Add(ArrayPool<byte>.Shared.Rent(SohBuffer.BlockSize));
                currentSegmentIndex++;
                currentSegmentOffset = 0;
            }

            var dest = segments[currentSegmentIndex].AsSpan(currentSegmentOffset, SohBuffer.BlockSize - currentSegmentOffset);
            var toCopy = Math.Min(remaining.Length, dest.Length);
            remaining[..toCopy].CopyTo(dest);
            currentSegmentOffset += toCopy;
            totalBytesWritten += toCopy;
            remaining = remaining[toCopy..];
        }
    }

    public async Task WriteTo(Stream destination, CancellationToken ct = default)
    {
        for (var i = 0; i <= currentSegmentIndex; i++)
        {
            var count = i == currentSegmentIndex ? currentSegmentOffset : SohBuffer.BlockSize;
            if (count > 0)
            {
                await destination.WriteAsync(segments[i].AsMemory(0, count), ct);
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var segment in segments)
            {
                ArrayPool<byte>.Shared.Return(segment);
            }
            segments.Clear();
            currentSegmentIndex = -1;
            currentSegmentOffset = 0;
        }

        base.Dispose(disposing);
    }
}
