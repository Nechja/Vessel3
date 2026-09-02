using System.Text;
using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class GarbageCollectorTests : IDisposable
{
    private readonly string root;
    private readonly string blobsRoot;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly BlobPool blobs;
    private readonly BucketRegistry registry;
    private readonly MultipartStore multipart;
    private readonly GcGate gate = new();
    private readonly GarbageCollector gc;

    public GarbageCollectorTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-gc-{Guid.NewGuid():N}");
        blobsRoot = Path.Combine(root, "blobs");
        Directory.CreateDirectory(root);
        blobs = new BlobPool(new BlobPoolOptions(blobsRoot), sync);
        registry = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        multipart = new MultipartStore(new MultipartStoreOptions(Path.Combine(root, "uploads")), registry, blobs, durable, gate);
        gc = new GarbageCollector(blobs, registry, multipart, gate, new GcOptions(TimeSpan.FromSeconds(30), Path.Combine(root, "gc-tmp")));
    }

    public void Dispose()
    {
        registry.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private string BlobPath(string sha) => Path.Combine(blobsRoot, sha[..2], sha[2..4], sha);

    [Fact]
    public async Task Gc_Keeps_Blobs_Of_A_Completed_Multipart_Object()
    {
        registry.Create("mybucket");
        var ct = TestContext.Current.CancellationToken;

        var upload = ((Result<CreateUploadOutcome>.Success)multipart.Create("mybucket", "big", "text/plain", new Dictionary<string, string>())).Value;
        var part = ((Result<UploadPartOutcome>.Success)await multipart.UploadPart(
            upload.UploadId, 1, new MemoryStream(Encoding.UTF8.GetBytes("the whole object")), null, ChecksumSet.Empty, ct)).Value;

        var complete = await multipart.Complete(
            upload.UploadId, [(1, part.Etag, null)], compositeAlgo: null, ct);
        Assert.IsType<Result<CompleteUploadOutcome>.Success>(complete);

        File.SetLastWriteTimeUtc(BlobPath(part.BlobSha), DateTime.UtcNow - TimeSpan.FromHours(2));

        var report = await gc.Run(minBlobAge: TimeSpan.FromHours(1), minUploadAge: TimeSpan.FromDays(7));

        Assert.Equal(0, report.BlobsDeleted);
        Assert.True(blobs.Exists(part.BlobSha));

        var current = ((Result<PutEntry?>.Success)registry.GetCurrentPut("mybucket", "big")).Value!;
        Assert.Equal(part.BlobSha, current.Parts![0].BlobSha);
    }

    [Fact]
    public void Reap_Removes_Aged_MetaLess_Orphan_But_Keeps_Live_Upload()
    {
        registry.Create("mybucket");
        var live = ((Result<CreateUploadOutcome>.Success)multipart.Create(
            "mybucket", "k", "text/plain", new Dictionary<string, string>())).Value;

        var uploadsRoot = Path.Combine(root, "uploads");
        var orphan = Path.Combine(uploadsRoot, "ORPHANUPLOADID0000000000000");
        Directory.CreateDirectory(Path.Combine(orphan, "parts"));
        File.WriteAllText(Path.Combine(orphan, "parts", "00001.json"), "{\"Number\":1,\"BlobSha\":\"abc\",\"Md5\":\"x\",\"Size\":1}");
        Directory.SetLastWriteTimeUtc(orphan, DateTime.UtcNow - TimeSpan.FromHours(2));

        var reaped = multipart.ReapAbandonedUploads(DateTime.UtcNow - TimeSpan.FromHours(1));

        Assert.Equal(1, reaped);
        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(Path.Combine(uploadsRoot, live.UploadId)));
    }

    [Fact]
    public async Task Gc_Deletes_An_Unreferenced_Aged_Blob()
    {
        var orphan = ((Result<StoredBlob>.Success)await blobs.Write(
            new MemoryStream(Encoding.UTF8.GetBytes("nobody points at me")), null, ChecksumIntent.None, TestContext.Current.CancellationToken)).Value;
        File.SetLastWriteTimeUtc(BlobPath(orphan.Sha), DateTime.UtcNow - TimeSpan.FromHours(2));

        var report = await gc.Run(minBlobAge: TimeSpan.FromHours(1), minUploadAge: TimeSpan.FromDays(7));

        Assert.Equal(1, report.BlobsDeleted);
        Assert.False(blobs.Exists(orphan.Sha));
    }

    [Fact]
    public async Task Gc_Sweeps_Across_Shards()
    {
        registry.Create("mybucket");
        var ct = TestContext.Current.CancellationToken;

        var kept = new List<string>();
        var orphaned = new List<string>();
        for (var i = 0; kept.Count < 4 || orphaned.Count < 4; i++)
        {
            var body = $"payload-{i}";
            var written = ((Result<StoredBlob>.Success)await blobs.Write(
                new MemoryStream(Encoding.UTF8.GetBytes(body)), null, ChecksumIntent.None, ct)).Value;
            File.SetLastWriteTimeUtc(BlobPath(written.Sha), DateTime.UtcNow - TimeSpan.FromHours(2));
            if (i % 2 == 0 && kept.Count < 4)
            {
                registry.AppendPut("mybucket", $"k{i}", new PutRequest(
                    written.Sha, written.Md5, body.Length, "text/plain", new Dictionary<string, string>()));
                kept.Add(written.Sha);
            }
            else
            {
                orphaned.Add(written.Sha);
            }
        }
        Assert.True(kept.Concat(orphaned).Select(s => s[..2]).Distinct().Count() > 1);

        var report = await gc.Run(minBlobAge: TimeSpan.FromHours(1), minUploadAge: TimeSpan.FromDays(7));

        Assert.Equal(orphaned.Count, report.BlobsDeleted);
        Assert.All(kept, sha => Assert.True(blobs.Exists(sha)));
        Assert.All(orphaned, sha => Assert.False(blobs.Exists(sha)));
    }
}
