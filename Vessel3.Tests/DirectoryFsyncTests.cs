using System.Text;
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
    public void Open_NewLog_FsyncsLogDirectory()
    {
        var rec = Recorder();
        var bucketPath = Path.Combine(root, "b");

        using var b = new Bucket("b", bucketPath, rec, durable);
        b.Open();

        Assert.Contains(Full(bucketPath), rec.DirSyncs);
    }

    [Fact]
    public void Open_Reopen_DoesNotRefsyncExistingLogDirectory()
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
    public async Task BlobPool_NewPrefix_FsyncsFanOutAncestors()
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
    public void Create_NewBucket_FsyncsBucketsRoot()
    {
        var rec = Recorder();
        var reg = new BucketRegistry(new BucketRegistryOptions(root), rec, durable);

        reg.Create("mybucket");

        Assert.Contains(Full(root, "buckets"), rec.DirSyncs);
    }

    [Fact]
    public void Create_FsyncFailure_PropagatesFailure()
    {
        var reg = new BucketRegistry(new BucketRegistryOptions(root), new FailingFileSync(int.MaxValue), durable);

        var res = reg.Create("mybucket");

        Assert.IsType<Result<bool>.Failure>(res);
    }

    [Fact]
    public void Open_TransientFsyncFailure_IsRetryable()
    {
        Directory.CreateDirectory(Path.Combine(root, "buckets", "mybucket"));
        var reg = new BucketRegistry(new BucketRegistryOptions(root), new FailingFileSync(1), durable);

        Assert.Throws<IOException>(() => reg.GetVersioning("mybucket"));
        Assert.IsType<Result<VersioningStatus>.Success>(reg.GetVersioning("mybucket"));
    }
}
