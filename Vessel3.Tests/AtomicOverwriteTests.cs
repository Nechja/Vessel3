using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class AtomicOverwriteTests : IDisposable
{
    private readonly string root;
    private readonly Bucket bucket;

    public AtomicOverwriteTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        bucket = new Bucket("atomic", root);
        bucket.Open();
    }

    public void Dispose()
    {
        bucket.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static PutRequest Req(string body) => new(
        BlobSha: Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))),
        Md5: Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(body))),
        Size: body.Length,
        ContentType: "text/plain",
        Metadata: new Dictionary<string, string>());

    [Fact]
    public async Task Unversioned_overwrite_never_observes_NoSuchKey()
    {
        bucket.AppendPut("k", Req("seed"));

        const int writers = 4;
        const int writesPerWriter = 200;
        const int readers = 8;
        var seenAbsent = 0;
        using var stop = new CancellationTokenSource();

        var readerTasks = Enumerable.Range(0, readers).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (bucket.Index.GetCurrentPut("k") is Result<PutEntry?>.Success { Value: null })
                    Interlocked.Increment(ref seenAbsent);
            }
        })).ToArray();

        var writerTasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < writesPerWriter; i++)
                bucket.AppendPut("k", Req($"w{w}-{i}"));
        })).ToArray();

        await Task.WhenAll(writerTasks);
        stop.Cancel();
        await Task.WhenAll(readerTasks);

        Assert.Equal(0, seenAbsent);
    }

    [Fact]
    public async Task Suspended_overwrite_never_observes_NoSuchKey()
    {
        bucket.SetVersioning(VersioningStatus.Suspended);
        bucket.AppendPut("k", Req("seed"));

        const int writers = 4;
        const int writesPerWriter = 200;
        const int readers = 8;
        var seenAbsent = 0;
        using var stop = new CancellationTokenSource();

        var readerTasks = Enumerable.Range(0, readers).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (bucket.Index.GetCurrentPut("k") is Result<PutEntry?>.Success { Value: null })
                    Interlocked.Increment(ref seenAbsent);
            }
        })).ToArray();

        var writerTasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < writesPerWriter; i++)
                bucket.AppendPut("k", Req($"w{w}-{i}"));
        })).ToArray();

        await Task.WhenAll(writerTasks);
        stop.Cancel();
        await Task.WhenAll(readerTasks);

        Assert.Equal(0, seenAbsent);
    }
}
