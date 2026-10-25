using System.Buffers;
using System.Collections.Frozen;

namespace Vessel3.IO;

internal sealed class PooledBufferStream : Stream
{
    private static readonly FrozenDictionary<SeekOrigin, Func<long, long, long, long>> SeekCalculators =
        new Dictionary<SeekOrigin, Func<long, long, long, long>>
        {
            [SeekOrigin.Begin] = static (offset, _, _) => offset,
            [SeekOrigin.Current] = static (offset, pos, _) => pos + offset,
            [SeekOrigin.End] = static (offset, _, len) => len + offset,
        }.ToFrozenDictionary();

    private readonly List<byte[]> segments = [];
    private int currentSegmentIndex = -1;
    private int currentSegmentOffset;
    private long totalBytesWritten;
    private long readPosition;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => totalBytesWritten;

    public override long Position
    {
        get => readPosition;
        set => Seek(value, SeekOrigin.Begin);
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
            readPosition = totalBytesWritten;
            remaining = remaining[toCopy..];
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || readPosition >= totalBytesWritten)
            return 0;

        var remaining = buffer;
        var totalRead = 0;

        while (!remaining.IsEmpty && readPosition < totalBytesWritten)
        {
            var segIndex = (int)(readPosition / SohBuffer.BlockSize);
            var segOffset = (int)(readPosition % SohBuffer.BlockSize);
            var segValidLength = segIndex == currentSegmentIndex
                ? currentSegmentOffset
                : SohBuffer.BlockSize;
            var availableInSegment = Math.Min(segValidLength - segOffset, (int)(totalBytesWritten - readPosition));
            var toCopy = Math.Min(remaining.Length, availableInSegment);

            segments[segIndex].AsSpan(segOffset, toCopy).CopyTo(remaining);
            remaining = remaining[toCopy..];
            readPosition += toCopy;
            totalRead += toCopy;
        }

        return totalRead;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<int>(cancellationToken)
            : ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (!SeekCalculators.TryGetValue(origin, out var calculate))
            throw new ArgumentOutOfRangeException(nameof(origin));

        var target = calculate(offset, readPosition, totalBytesWritten);
        ArgumentOutOfRangeException.ThrowIfNegative(target);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(target, totalBytesWritten);
        readPosition = target;
        return readPosition;
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
            readPosition = 0;
        }

        base.Dispose(disposing);
    }
}
