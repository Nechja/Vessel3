using System.Text;
using Vessel3.Client;
using Xunit;

namespace Vessel3.Tests;

public sealed class ProgressStreamTests
{
    [Fact]
    public void ProgressStream_ReadsSynchronously_ReportsProgress()
    {
        var data = Encoding.UTF8.GetBytes("hello progress streaming world");
        using var memory = new MemoryStream(data);
        var reported = new List<long>();
        var progress = new Progress<long>(reported.Add);

        using var progressStream = new ProgressStream(memory, progress);
        var buffer = new byte[8];
        var totalRead = 0;
        int read;
        while ((read = progressStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            totalRead += read;
        }

        Assert.Equal(data.Length, totalRead);
        Assert.Equal(data.Length, progressStream.Position);
        Assert.True(progressStream.CanRead);
        Assert.True(progressStream.CanSeek);
        Assert.False(progressStream.CanWrite);
    }

    [Fact]
    public async Task ProgressStream_ReadsAsynchronously_ReportsProgress()
    {
        var data = Encoding.UTF8.GetBytes(new string('x', 65536));
        using var memory = new MemoryStream(data);
        var lastReported = 0L;
        var progress = new Progress<long>(b => lastReported = b);

        await using var progressStream = new ProgressStream(memory, progress);
        var buffer = new byte[8192];
        var totalRead = 0;
        int read;
        while ((read = await progressStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            totalRead += read;
        }

        Assert.Equal(data.Length, totalRead);
        Assert.Equal(data.Length, progressStream.Length);
    }

    [Fact]
    public void ProgressStream_DelegatesSeekAndLength()
    {
        var data = Encoding.UTF8.GetBytes("seekable content");
        using var memory = new MemoryStream(data);
        var progress = new Progress<long>(_ => { });

        using var progressStream = new ProgressStream(memory, progress);
        Assert.Equal(data.Length, progressStream.Length);

        progressStream.Seek(4, SeekOrigin.Begin);
        Assert.Equal(4, progressStream.Position);

        var b = progressStream.ReadByte();
        Assert.Equal((int)'a', b);
    }

    [Fact]
    public void ProgressStream_Disposal_DisposesUnderlyingStream()
    {
        var memory = new DisposableMemoryStream();
        var progress = new Progress<long>(_ => { });

        var progressStream = new ProgressStream(memory, progress);
        progressStream.Dispose();

        Assert.True(memory.IsDisposed);
    }

    [Fact]
    public void ProgressStream_RewindAndSeek_SynchronizesReportedBytes()
    {
        var data = Encoding.UTF8.GetBytes("0123456789abcdef");
        using var memory = new MemoryStream(data);
        var syncProgress = new SyncProgress();
        using var progressStream = new ProgressStream(memory, syncProgress);

        var buf = new byte[10];
        var read = progressStream.Read(buf, 0, 10);
        Assert.Equal(10, read);
        Assert.Equal(10, syncProgress.Reports.Last());

        progressStream.Position = 0;
        Assert.Equal(0, syncProgress.Reports.Last());

        read = progressStream.Read(buf, 0, 5);
        Assert.Equal(5, read);
        Assert.Equal(5, syncProgress.Reports.Last());

        progressStream.Seek(2, SeekOrigin.Begin);
        Assert.Equal(2, syncProgress.Reports.Last());

        read = progressStream.Read(buf, 0, 2);
        Assert.Equal(2, read);
        Assert.Equal(4, syncProgress.Reports.Last());
    }

    private sealed class SyncProgress : IProgress<long>
    {
        public List<long> Reports { get; } = [];
        public void Report(long value) => Reports.Add(value);
    }

    private sealed class DisposableMemoryStream : MemoryStream
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
