using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class DiskFullTests : IDisposable
{
    private readonly string root;
    private readonly BlobPool pool;

    public DiskFullTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-diskfull-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        pool = new BlobPool(new BlobPoolOptions(root));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private sealed class EnospcStream(int failAtByte) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var span = buffer.AsSpan(offset, count);
            var i = 0;
            while (i < span.Length && position < failAtByte)
            {
                span[i++] = (byte)(position & 0xFF);
                position++;
            }
            if (i > 0) return i;
            if (position >= failAtByte) throw new IOException("No space left on device");
            return 0;
        }
    }

    [Fact]
    public async Task BlobPool_NoSpaceMidWrite_ReturnsInsufficientStorage_AndCleansTemp()
    {
        var src = new EnospcStream(failAtByte: 100_000);
        var r = await pool.Write(src, declaredSize: null, ChecksumIntent.All, CancellationToken.None);
        Assert.IsType<Result<StoredBlob>.Failure>(r);
        var err = ((Result<StoredBlob>.Failure)r).Error;
        Assert.IsType<InsufficientStorageError>(err);

        var tmpDir = Path.Combine(root, "tmp");
        if (Directory.Exists(tmpDir))
            Assert.Empty(Directory.GetFiles(tmpDir));
    }

    [Fact]
    public async Task BlobPool_RecoveryAfterEnospc_NextWriteSucceeds()
    {
        var fail = await pool.Write(new EnospcStream(failAtByte: 100), declaredSize: null, ChecksumIntent.All, CancellationToken.None);
        Assert.IsType<Result<StoredBlob>.Failure>(fail);

        using var ok = new MemoryStream("recovered"u8.ToArray());
        var r = await pool.Write(ok, declaredSize: null, ChecksumIntent.All, CancellationToken.None);
        Assert.IsType<Result<StoredBlob>.Success>(r);
    }
}
