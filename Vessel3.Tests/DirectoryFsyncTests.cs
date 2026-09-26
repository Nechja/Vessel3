using System.Text;
using Vessel3.Server;
using Xunit;

namespace Vessel3.Tests;

public class DirectoryFsyncTests : IDisposable
{
    private readonly string root;
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());

    public DirectoryFsyncTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-fsync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private sealed class RecordingFileSync(IFileSync inner) : IFileSync
    {
        public List<string> DirSyncs { get; } = [];
        public Result SyncData(FileStream file) => inner.SyncData(file);
        public Result SyncDirectory(string directory)
        {
            DirSyncs.Add(Path.GetFullPath(directory));
            return inner.SyncDirectory(directory);
        }
    }

    private sealed class FailingFileSync(int failFirst) : IFileSync
    {
        private int remaining = failFirst;
        public Result SyncData(FileStream file) => Result.Ok;
        public Result SyncDirectory(string directory)
            => remaining-- > 0 ? new DurabilityError("injected") : Result.Ok;
    }

    private static RecordingFileSync Recorder() => new(new PortableFileSync());
    private static string Full(params string[] parts) => Path.GetFullPath(Path.Combine(parts));

    [Fact]
    public void Bucket_Open_Fsyncs_Log_Directory_When_Log_Is_New()
    {
        var rec = Recorder();
        var bucketPath = Path.Combine(root, "b");

        using var b = new Bucket("b", bucketPath, rec, durable);
        b.Open();

        Assert.Contains(Full(bucketPath), rec.DirSyncs);
    }

    [Fact]
    public void Bucket_Reopen_Does_Not_Refsync_Existing_Log_Directory()
    {
        var bucketPath = Path.Combine(root, "b");
        using (var b = new Bucket("b", bucketPath, Recorder(), durable))
            b.Open();

        var rec = Recorder();
        using (var b = new Bucket("b", bucketPath, rec, durable))
            b.Open();

        Assert.DoesNotContain(Full(bucketPath), rec.DirSyncs);
    }

    [Fact]
    public async Task BlobPool_Fsyncs_FanOut_Ancestors_For_New_Prefix()
    {
        var rec = Recorder();
        var pool = new BlobPool(new BlobPoolOptions(root), rec);

        var res = await pool.Write(new MemoryStream(Encoding.UTF8.GetBytes("hello")), null, ChecksumIntent.None, TestContext.Current.CancellationToken);
        var sha = ((Result<StoredBlob>.Success)res).Value.Sha;

        Assert.Contains(Full(root), rec.DirSyncs);
        Assert.Contains(Full(root, sha[..2]), rec.DirSyncs);
        Assert.Contains(Full(root, sha[..2], sha[2..4]), rec.DirSyncs);
    }

    [Fact]
    public void BucketRegistry_Create_Fsyncs_Buckets_Root()
    {
        var rec = Recorder();
        var reg = new BucketRegistry(new BucketRegistryOptions(root), rec, durable);

        reg.Create("mybucket");

        Assert.Contains(Full(root, "buckets"), rec.DirSyncs);
    }

    [Fact]
    public void BucketRegistry_Create_Propagates_Directory_Fsync_Failure()
    {
        var reg = new BucketRegistry(new BucketRegistryOptions(root), new FailingFileSync(int.MaxValue), durable);

        var res = reg.Create("mybucket");

        Assert.IsType<Result<bool>.Failure>(res);
    }

    [Fact]
    public void Bucket_Open_Is_Retryable_After_A_Transient_Fsync_Failure()
    {
        Directory.CreateDirectory(Path.Combine(root, "buckets", "mybucket"));
        var reg = new BucketRegistry(new BucketRegistryOptions(root), new FailingFileSync(1), durable);

        Assert.Throws<IOException>(() => reg.GetVersioning("mybucket"));
        Assert.IsType<Result<VersioningStatus>.Success>(reg.GetVersioning("mybucket"));
    }
}
