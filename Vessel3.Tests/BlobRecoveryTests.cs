using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

// Complements PowerLossTests/DurabilityReplayTests (which exercise the version-log/index
// recovery with synthetic shas) by driving real blobs through BlobPool and asserting the
// blob<->metadata join: acked bytes survive a restart, and recovery never leaves an index
// entry pointing at a missing blob.
public class BlobRecoveryTests : IDisposable
{
    private readonly string root;
    private readonly string blobsDir;

    public BlobRecoveryTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-blobrec-{Guid.NewGuid():N}");
        blobsDir = Path.Combine(root, "blobs");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private BlobPool NewBlobPool() => new(new BlobPoolOptions(blobsDir));

    private static async Task<StoredBlob> Store(IBlobPool pool, byte[] body) =>
        ((Result<StoredBlob>.Success)await pool.Write(
            new MemoryStream(body), body.Length, ChecksumIntent.All, CancellationToken.None)).Value;

    private static PutRequest Req(StoredBlob blob) => new(
        BlobSha: blob.Sha, Md5: blob.Md5, Size: blob.Size,
        ContentType: "application/octet-stream", Metadata: new Dictionary<string, string>());

    private static byte[] ReadBlob(IBlobPool pool, string sha)
    {
        using var s = ((Result<Stream>.Success)pool.Open(sha)).Value;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void WipeIndex(string root)
    {
        foreach (var f in Directory.GetFiles(root, "index*")) File.Delete(f);
    }

    private static void AssertNoDanglingReferences(Bucket b, IBlobPool pool)
    {
        foreach (var sha in b.Index.ReferencedBlobs())
            Assert.True(pool.Exists(sha), $"index references blob with no readable file: {sha}");
    }

    [Fact]
    public async Task Acked_put_recovers_blob_bytes_after_restart()
    {
        var body = "durable-payload"u8.ToArray();
        var pool = NewBlobPool();
        var blob = await Store(pool, body);
        using (var b = new Bucket("b", root))
        {
            b.Open();
            b.AppendPut("k", Req(blob));
        }

        using var restored = new Bucket("b", root);
        restored.Open();
        var entry = ((Result<PutEntry?>.Success)restored.Index.GetCurrentPut("k")).Value!;
        Assert.Equal(blob.Sha, entry.BlobSha);
        Assert.Equal(body, ReadBlob(pool, entry.BlobSha));
        AssertNoDanglingReferences(restored, pool);
    }

    [Fact]
    public async Task Blob_recovers_when_only_log_survives()
    {
        var body = "rebuilt-from-log"u8.ToArray();
        var pool = NewBlobPool();
        var blob = await Store(pool, body);
        using (var b = new Bucket("b", root))
        {
            b.Open();
            b.AppendPut("k", Req(blob));
        }

        WipeIndex(root); // index lost; only the version log + blob files survive

        using var restored = new Bucket("b", root);
        restored.Open();
        var entry = ((Result<PutEntry?>.Success)restored.Index.GetCurrentPut("k")).Value!;
        Assert.Equal(body, ReadBlob(pool, entry.BlobSha));
        AssertNoDanglingReferences(restored, pool);
    }

    [Fact]
    public async Task Blob_without_log_record_is_unreferenced_orphan()
    {
        var pool = NewBlobPool();
        var orphan = await Store(pool, "never-acked"u8.ToArray()); // written, no AppendPut
        using (var b = new Bucket("b", root))
        {
            b.Open();
            b.AppendPut("k", Req(await Store(pool, "real"u8.ToArray())));
        }

        using var restored = new Bucket("b", root);
        restored.Open();
        Assert.True(pool.Exists(orphan.Sha));                                  // bytes on disk
        Assert.DoesNotContain(orphan.Sha, restored.Index.ReferencedBlobs());   // but GC-collectable, never referenced
    }

    [Fact]
    public async Task Torn_log_tail_drops_record_and_leaves_no_dangling_reference()
    {
        var pool = NewBlobPool();
        var blobA = await Store(pool, "AAAAAAAA"u8.ToArray());
        var blobB = await Store(pool, "BBBBBBBB"u8.ToArray());

        long sizeAfterA;
        using (var b = new Bucket("b", root))
        {
            b.Open();
            b.AppendPut("a", Req(blobA));
            sizeAfterA = new FileInfo(Path.Combine(root, "log")).Length;
            b.AppendPut("bk", Req(blobB));
        }

        // simulate a crash mid-write of B's log record: truncate back to the end of A's record
        using (var fs = new FileStream(Path.Combine(root, "log"), FileMode.Open, FileAccess.Write))
            fs.SetLength(sizeAfterA);
        WipeIndex(root); // force a full replay over the truncated log

        using var restored = new Bucket("b", root);
        restored.Open();
        Assert.NotNull(((Result<PutEntry?>.Success)restored.Index.GetCurrentPut("a")).Value);
        Assert.False(restored.Index.GetCurrentPut("bk") is Result<PutEntry?>.Success { Value: not null });
        Assert.Equal("AAAAAAAA"u8.ToArray(),
            ReadBlob(pool, ((Result<PutEntry?>.Success)restored.Index.GetCurrentPut("a")).Value!.BlobSha));
        AssertNoDanglingReferences(restored, pool); // B's blob is now an orphan, not a dangling ref
    }
}
