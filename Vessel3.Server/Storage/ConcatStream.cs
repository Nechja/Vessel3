namespace Vessel3.Server.Storage;

internal sealed class ConcatStream(IReadOnlyList<MultipartPart> parts, IBlobPool blobs) : Stream
{
    private readonly long totalLength = SumSizes(parts);
    private long position;
    private int currentIndex = -1;
    private long currentPartStart;
    private Stream? currentStream;

    private static long SumSizes(IReadOnlyList<MultipartPart> parts)
    {
        long sum = 0;
        foreach (var p in parts) sum += p.Size;
        return sum;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => totalLength;

    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count is 0 || position >= totalLength) return 0;
        EnsureCurrent();
        if (currentStream is null) return 0;

        var n = currentStream.Read(buffer, offset, count);
        if (n is 0)
        {
            AdvancePart();
            EnsureCurrent();
            if (currentStream is null) return 0;
            n = currentStream.Read(buffer, offset, count);
        }
        position += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length is 0 || position >= totalLength) return 0;
        EnsureCurrent();
        if (currentStream is null) return 0;

        var n = await currentStream.ReadAsync(buffer, ct);
        if (n is 0)
        {
            AdvancePart();
            EnsureCurrent();
            if (currentStream is null) return 0;
            n = await currentStream.ReadAsync(buffer, ct);
        }
        position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => totalLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0 || target > totalLength)
            throw new ArgumentOutOfRangeException(nameof(offset));

        if (currentStream is not null && currentIndex >= 0 && currentIndex < parts.Count)
        {
            var partEnd = currentPartStart + parts[currentIndex].Size;
            if (target >= currentPartStart && target < partEnd)
            {
                currentStream.Seek(target - currentPartStart, SeekOrigin.Begin);
                position = target;
                return position;
            }
        }

        position = target;
        DisposeCurrent();
        currentIndex = -1;
        return position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeCurrent();
        base.Dispose(disposing);
    }

    private void EnsureCurrent()
    {
        if (currentStream is not null) return;
        if (position >= totalLength) return;

        if (currentIndex < 0)
        {
            long offset = 0;
            for (var i = 0; i < parts.Count; i++)
            {
                var partEnd = offset + parts[i].Size;
                if (position < partEnd)
                {
                    currentIndex = i;
                    currentPartStart = offset;
                    break;
                }
                offset = partEnd;
            }
        }

        if (currentIndex < 0 || currentIndex >= parts.Count) return;
        currentStream = OpenPart(parts[currentIndex]);
        var skip = position - currentPartStart;
        if (skip > 0) currentStream.Seek(skip, SeekOrigin.Begin);
    }

    private void AdvancePart()
    {
        DisposeCurrent();
        if (currentIndex >= 0 && currentIndex < parts.Count)
        {
            currentPartStart += parts[currentIndex].Size;
            currentIndex++;
        }
    }

    private Stream OpenPart(MultipartPart part) =>
        blobs.Open(part.BlobSha) is Result<Stream>.Success ok
            ? ok.Value
            : throw new InvalidOperationException($"missing blob for part {part.Number}: {part.BlobSha}");

    private void DisposeCurrent()
    {
        currentStream?.Dispose();
        currentStream = null;
    }
}
