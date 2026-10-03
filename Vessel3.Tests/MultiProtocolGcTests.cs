using System.Text;
using Vessel3.Protocols.Oci;
using Vessel3.Storage;
using Xunit;

namespace Vessel3.Tests;

public class MultiProtocolGcTests : IDisposable
{
    private readonly string root;
    private readonly string blobsRoot;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly BlobPool blobs;
    private readonly BucketRegistry s3Registry;
    private readonly SqliteContainerRepoCatalog ociCatalog;
    private readonly ChunkStager stager;
    private readonly GcGate gate = new();
    private readonly GarbageCollector gc;

    public MultiProtocolGcTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-multi-gc-{Guid.NewGuid():N}");
        blobsRoot = Path.Combine(root, "blobs");
        Directory.CreateDirectory(root);

        blobs = new BlobPool(new BlobPoolOptions(blobsRoot), sync);
        s3Registry = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        ociCatalog = new SqliteContainerRepoCatalog(new ContainerRepoCatalogOptions(Path.Combine(root, "oci")));
        stager = new ChunkStager(new ChunkStagerOptions(Path.Combine(root, "uploads")), s3Registry, blobs, durable, gate);

        IBlobReferenceSource[] sources = [s3Registry, ociCatalog];
        gc = new GarbageCollector(blobs, sources, stager, gate, new GcOptions(TimeSpan.FromSeconds(30), Path.Combine(root, "gc-tmp")));
    }

    public void Dispose()
    {
        s3Registry.Dispose();
        ociCatalog.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private string BlobPath(string sha) => Path.Combine(blobsRoot, sha[..2], sha[2..4], sha);

    [Fact]
    public async Task Gc_Preserves_Blobs_From_Both_S3_And_ContainerRepos_While_Deleting_Orphans()
    {
        var ct = TestContext.Current.CancellationToken;

        var s3Data = Encoding.UTF8.GetBytes("s3-object-content");
        var blobS3 = ((Result<StoredBlob>.Success)await blobs.Write(new MemoryStream(s3Data), s3Data.Length, ChecksumIntent.None, ct)).Value;

        s3Registry.Create("my-bucket");
        s3Registry.AppendPut("my-bucket", "obj1", new PutRequest(blobS3.Sha, blobS3.Md5, blobS3.Size, "text/plain", new Dictionary<string, string>()));

        var ociLayerData = Encoding.UTF8.GetBytes("oci-layer-tar-content");
        var blobOci = ((Result<StoredBlob>.Success)await blobs.Write(new MemoryStream(ociLayerData), ociLayerData.Length, ChecksumIntent.None, ct)).Value;

        var manifestJson = Encoding.UTF8.GetBytes($$"""
        {
            "schemaVersion": 2,
            "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
            "layers": [
                {
                    "mediaType": "application/vnd.docker.image.rootfs.diff.tar.gzip",
                    "size": {{ociLayerData.Length}},
                    "digest": "sha256:{{blobOci.Sha}}"
                }
            ]
        }
        """);
        var putRes = ociCatalog.PutManifest("my-app", "v1.0.0", "application/vnd.docker.distribution.manifest.v2+json", manifestJson, [$"sha256:{blobOci.Sha}"]);
        Assert.True(putRes.TryGetValue(out var manifestOutcome, out _));
        await blobs.Write(new MemoryStream(manifestJson), manifestJson.Length, ChecksumIntent.None, ct);

        var orphanData = Encoding.UTF8.GetBytes("unreferenced-orphan-data");
        var blobOrphan = ((Result<StoredBlob>.Success)await blobs.Write(new MemoryStream(orphanData), orphanData.Length, ChecksumIntent.None, ct)).Value;

        File.SetLastWriteTimeUtc(BlobPath(blobS3.Sha), DateTime.UtcNow - TimeSpan.FromHours(2));
        File.SetLastWriteTimeUtc(BlobPath(blobOci.Sha), DateTime.UtcNow - TimeSpan.FromHours(2));
        File.SetLastWriteTimeUtc(BlobPath(blobOrphan.Sha), DateTime.UtcNow - TimeSpan.FromHours(2));
        File.SetLastWriteTimeUtc(BlobPath(manifestOutcome.Digest[7..]), DateTime.UtcNow - TimeSpan.FromHours(2));

        var report = await gc.Run(minBlobAge: TimeSpan.FromHours(1), minUploadAge: TimeSpan.FromDays(7));

        Assert.Equal(1, report.BlobsDeleted);
        Assert.True(blobs.Exists(blobS3.Sha), "S3 blob must be preserved");
        Assert.True(blobs.Exists(blobOci.Sha), "Container Repo layer blob must be preserved");
        Assert.True(blobs.Exists(manifestOutcome.Digest[7..]), "Container Repo manifest blob must be preserved");
        Assert.False(blobs.Exists(blobOrphan.Sha), "Orphan blob must be deleted");
    }
}
