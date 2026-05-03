namespace Vessel3.Server.Storage;

internal sealed class BoundedStream(Stream inner, long limit) : Stream
{
    private readonly long total = limit;
    private long remaining = limit;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => total;
    public override long Position { get => total - remaining; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (remaining <= 0) return 0;
        var take = (int)Math.Min(count, remaining);
        var n = inner.Read(buffer, offset, take);
        remaining -= n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (remaining <= 0) return 0;
        var take = (int)Math.Min(buffer.Length, remaining);
        var n = await inner.ReadAsync(buffer[..take], ct);
        remaining -= n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
