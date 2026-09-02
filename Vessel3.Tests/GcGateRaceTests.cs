using System.Text;
using Microsoft.AspNetCore.Http;
using Vessel3.Server;
using Vessel3.Server.Lifecycle;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class GcGateRaceTests : IDisposable
{
    private readonly string root;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly BlobPool blobs;
    private readonly BucketRegistry registry;
    private readonly GcGate gate = new();
    private static readonly TimeSpan GcMaxWait = TimeSpan.FromSeconds(2);

    public GcGateRaceTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-gcrace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        blobs = new BlobPool(new BlobPoolOptions(Path.Combine(root, "blobs")), sync);
        registry = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
    }

    public void Dispose()
    {
        registry.Dispose();
        gate.Dispose();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private GarbageCollector NewCollector(IBucketRegistry seen)
    {
        var multipart = new MultipartStore(
            new MultipartStoreOptions(Path.Combine(root, "uploads")), seen, blobs, durable, gate);
        return new GarbageCollector(blobs, seen, multipart, gate, new GcOptions(GcMaxWait, Path.Combine(root, "gc-tmp")));
    }

    [Fact]
    public async Task Gc_Cannot_Delete_A_Blob_Published_But_Not_Yet_Committed()
    {
        var ct = TestContext.Current.CancellationToken;
        registry.Create("landing");

        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = new PausingBlobPool(blobs, published, resume.Task);
        var objects = new ObjectStore(registry, paused, new PreconditionEvaluator(), gate);
        var collector = NewCollector(registry);

        var payload = Encoding.UTF8.GetBytes("bytes that must survive the sweep");
        var putTask = objects.Put("landing", "k", new MemoryStream(payload), payload.Length, "text/plain",
            null, null, new Dictionary<string, string>(), new Dictionary<string, string>(), ChecksumSet.Empty, ct);

        await published.Task;
        var publishedSha = paused.PublishedSha!;
        Assert.True(blobs.Exists(publishedSha));

        var report = await collector.Run(TimeSpan.Zero, TimeSpan.FromDays(7));

        Assert.True(blobs.Exists(publishedSha), "GC deleted a blob that was published but not yet committed");
        Assert.Equal(0, report.BlobsDeleted);
        Assert.True(report.TimedOut);

        resume.SetResult();
        var put = Assert.IsType<Result<PutOutcome>.Success>(await putTask).Value;

        Assert.True(blobs.Exists(put.Sha256));
        var stored = Assert.IsType<Result<StoredObject>.Success>(objects.Get("landing", "k")).Value;
        using var body = stored.Body;
        var read = new MemoryStream();
        await body.CopyToAsync(read, ct);
        Assert.Equal(payload, read.ToArray());
    }

    [Fact]
    public async Task Gc_Cannot_Delete_A_Blob_Whose_Only_Reference_Is_Moving_Between_Buckets()
    {
        var ct = TestContext.Current.CancellationToken;
        registry.Create("alpha-dest");
        registry.Create("zulu-src");

        var objects = new ObjectStore(registry, blobs, new PreconditionEvaluator(), gate);
        var payload = Encoding.UTF8.GetBytes("the only copy of this object");
        var seeded = Assert.IsType<Result<PutOutcome>.Success>(await objects.Put(
            "zulu-src", "k", new MemoryStream(payload), payload.Length, "text/plain",
            null, null, new Dictionary<string, string>(), new Dictionary<string, string>(),
            ChecksumSet.Empty, ct)).Value;

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalling = new StallingRegistry(registry, entered, resume.Task);
        var copier = new ObjectStore(stalling, blobs, new PreconditionEvaluator(), gate);
        var collector = NewCollector(registry);

        stalling.Arm();
        var copyTask = Task.Run(() => copier.Copy("alpha-dest", "k", "zulu-src", "k", new HeaderDictionary(), null, null), ct);
        await entered.Task;

        Assert.IsType<Result<DeleteOutcome>.Success>(registry.HardDeleteVersion("zulu-src", "k", seeded.VersionId, false));
        Assert.DoesNotContain(seeded.Sha256, registry.AllReferencedBlobs());

        var report = await collector.Run(TimeSpan.Zero, TimeSpan.FromDays(7));

        Assert.True(blobs.Exists(seeded.Sha256), "GC deleted a blob whose only reference was moving between buckets");
        Assert.Equal(0, report.BlobsDeleted);
        Assert.True(report.TimedOut);

        resume.SetResult();
        Assert.IsType<Result<CopyOutcome>.Success>(await copyTask);

        Assert.True(blobs.Exists(seeded.Sha256));
        var stored = Assert.IsType<Result<StoredObject>.Success>(objects.Get("alpha-dest", "k")).Value;
        using var body = stored.Body;
        var read = new MemoryStream();
        await body.CopyToAsync(read, ct);
        Assert.Equal(payload, read.ToArray());
    }

    [Fact]
    public async Task Gc_Runs_Immediately_When_No_Writer_Holds_The_Gate()
    {
        var orphan = Assert.IsType<Result<StoredBlob>.Success>(await blobs.Write(
            new MemoryStream(Encoding.UTF8.GetBytes("garbage")), null, ChecksumIntent.None,
            TestContext.Current.CancellationToken)).Value;

        var report = await NewCollector(registry).Run(TimeSpan.Zero, TimeSpan.FromDays(7));

        Assert.False(report.TimedOut);
        Assert.Equal(1, report.BlobsDeleted);
        Assert.False(blobs.Exists(orphan.Sha));
    }

    private sealed class PausingBlobPool(IBlobPool inner, TaskCompletionSource published, Task resume) : IBlobPool
    {
        public string? PublishedSha { get; private set; }

        public async Task<Result<StoredBlob>> Write(Stream source, long? declaredSize, ChecksumIntent intent, CancellationToken ct)
        {
            var written = await inner.Write(source, declaredSize, intent, ct);
            if (written is Result<StoredBlob>.Success ok) PublishedSha = ok.Value.Sha;
            published.TrySetResult();
            await resume;
            return written;
        }

        public Result<Stream> Open(string sha) => inner.Open(sha);
        public bool Exists(string sha) => inner.Exists(sha);
        public Result<bool> Delete(string sha) => inner.Delete(sha);
        public IEnumerable<string> EnumerateShards() => inner.EnumerateShards();
        public IEnumerable<string> Enumerate(string shard) => inner.Enumerate(shard);
        public DateTime? GetLastWriteUtc(string sha) => inner.GetLastWriteUtc(sha);
    }

    private sealed class StallingRegistry(IBucketRegistry inner, TaskCompletionSource entered, Task resume) : IBucketRegistry
    {
        private int armed;

        public void Arm() => Interlocked.Exchange(ref armed, 1);

        public Result<PutEntry> AppendPut(string bucket, string key, PutRequest req)
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                entered.TrySetResult();
                resume.GetAwaiter().GetResult();
            }
            return inner.AppendPut(bucket, key, req);
        }

        public bool IsValidName(string bucket) => inner.IsValidName(bucket);
        public Result<bool> Create(string bucket) => inner.Create(bucket);
        public Result Delete(string bucket) => inner.Delete(bucket);
        public Result<bool> Exists(string bucket) => inner.Exists(bucket);
        public IEnumerable<BucketInfo> List() => inner.List();
        public Result<PutEntry?> GetCurrentPut(string bucket, string key) => inner.GetCurrentPut(bucket, key);
        public Result<PutEntry?> GetVersion(string bucket, string key, string versionId) => inner.GetVersion(bucket, key, versionId);
        public Result<DeleteOutcome> AppendDelete(string bucket, string key, bool bypassGovernance) => inner.AppendDelete(bucket, key, bypassGovernance);
        public Result<DeleteOutcome> HardDeleteVersion(string bucket, string key, string versionId, bool bypassGovernance) => inner.HardDeleteVersion(bucket, key, versionId, bypassGovernance);
        public Result<IReadOnlyList<Result<DeleteOutcome>>> DeleteBatch(string bucket, IReadOnlyList<BatchDeleteItem> items) => inner.DeleteBatch(bucket, items);
        public Result<List<VersionListEntry>> ListCurrent(string bucket, string? prefix, string? startAfter) => inner.ListCurrent(bucket, prefix, startAfter);
        public Result<VersionsPage> ListAllVersions(string bucket, string? prefix, string? keyMarker, int limit) => inner.ListAllVersions(bucket, prefix, keyMarker, limit);
        public Result<VersioningStatus> GetVersioning(string bucket) => inner.GetVersioning(bucket);
        public Result SetVersioning(string bucket, VersioningStatus status) => inner.SetVersioning(bucket, status);
        public Result<PutTaggingOutcome> PutTagging(string bucket, string key, string? versionId, IReadOnlyDictionary<string, string> tags) => inner.PutTagging(bucket, key, versionId, tags);
        public VersionKind? GetCurrentKind(string bucket, string key) => inner.GetCurrentKind(bucket, key);
        public VersionKind? GetVersionKind(string bucket, string key, string versionId) => inner.GetVersionKind(bucket, key, versionId);
        public Result<ObjectLockConfig?> GetObjectLock(string bucket) => inner.GetObjectLock(bucket);
        public Result SetObjectLock(string bucket, ObjectLockConfig cfg) => inner.SetObjectLock(bucket, cfg);
        public Result<LifecycleConfig?> GetLifecycle(string bucket) => inner.GetLifecycle(bucket);
        public Result SetLifecycle(string bucket, LifecycleConfig cfg) => inner.SetLifecycle(bucket, cfg);
        public Result RemoveLifecycle(string bucket) => inner.RemoveLifecycle(bucket);
        public IEnumerable<Bucket> OpenBuckets() => inner.OpenBuckets();
        public Result PutRetention(string bucket, string key, string versionId, Retention retention, bool bypassGovernance) => inner.PutRetention(bucket, key, versionId, retention, bypassGovernance);
        public Result<Retention?> GetRetention(string bucket, string key, string versionId) => inner.GetRetention(bucket, key, versionId);
        public Result PutLegalHold(string bucket, string key, string versionId, bool on) => inner.PutLegalHold(bucket, key, versionId, on);
        public Result<bool> GetLegalHold(string bucket, string key, string versionId) => inner.GetLegalHold(bucket, key, versionId);
        public IEnumerable<string> AllReferencedBlobs() => inner.AllReferencedBlobs();
        public void Dispose() { }
    }
}
