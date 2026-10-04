using System.Text;
using Xunit;

namespace Vessel3.Tests;

public sealed class ChunkStagerTests : IDisposable
{
    private readonly string root;
    private readonly string blobsRoot;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly BlobPool blobs;
    private readonly BucketRegistry registry;
    private readonly GcGate gate = new();
    private readonly ChunkStager stager;

    public ChunkStagerTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-stager-{Guid.NewGuid():N}");
        blobsRoot = Path.Combine(root, "blobs");
        Directory.CreateDirectory(root);
        blobs = new BlobPool(new BlobPoolOptions(blobsRoot), sync);
        registry = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        stager = new ChunkStager(new ChunkStagerOptions(Path.Combine(root, "staging")), registry, blobs, durable, gate);
        registry.Create("test-bucket");
    }

    public void Dispose()
    {
        registry.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Session_FullLifecycle_CreatesStagesAndCommits()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = Assert.IsType<Result<StagedSession>.Success>(
            stager.CreateSession("test-bucket", "staged.txt", "text/plain", new Dictionary<string, string>())).Value;

        var chunk1Data = Encoding.UTF8.GetBytes("chunk-one-content");
        var chunk1 = Assert.IsType<Result<StagedChunk>.Success>(
            await stager.StageChunk(session.SessionId, "block-A", new MemoryStream(chunk1Data), chunk1Data.Length, DeclaredChecksums.Empty, ct)).Value;

        var chunk2Data = Encoding.UTF8.GetBytes("chunk-two-content");
        var chunk2 = Assert.IsType<Result<StagedChunk>.Success>(
            await stager.StageChunk(session.SessionId, "block-B", new MemoryStream(chunk2Data), chunk2Data.Length, DeclaredChecksums.Empty, ct)).Value;

        var chunks = Assert.IsType<Result<IReadOnlyList<StagedChunk>>.Success>(
            stager.ListChunks(session.SessionId)).Value;
        Assert.Equal(2, chunks.Count);
        Assert.True(chunks[0].StagedAt > DateTimeOffset.MinValue);

        List<MultipartPart> parts =
        [
            new(1, chunk1.BlobSha, chunk1.Md5, chunk1.Size),
            new(2, chunk2.BlobSha, chunk2.Md5, chunk2.Size)
        ];

        var outcome = Assert.IsType<Result<CommitChunksOutcome>.Success>(
            await stager.Commit(session.SessionId, parts, "custom-etag", ChecksumSet.Empty, ct)).Value;

        Assert.Equal(chunk1Data.Length + chunk2Data.Length, outcome.TotalSize);
        Assert.NotNull(outcome.VersionId);

        var put = Assert.IsType<Result<PutEntry?>.Success>(registry.GetCurrentPut("test-bucket", "staged.txt")).Value;
        Assert.NotNull(put);
        Assert.Equal("custom-etag", put.Md5);
        Assert.Equal(2, put.Parts!.Count);

        Assert.IsType<Result<StagedSession>.Failure>(stager.GetSession(session.SessionId));
    }

    [Fact]
    public void Abort_ExistingSession_DeletesSessionDirectory()
    {
        var session = Assert.IsType<Result<StagedSession>.Success>(
            stager.CreateSession("test-bucket", "abort.txt", "text/plain", new Dictionary<string, string>())).Value;

        var abortResult = stager.AbortSession(session.SessionId);
        Assert.IsType<Result.OkResult>(abortResult);

        Assert.IsType<Result<StagedSession>.Failure>(stager.GetSession(session.SessionId));
    }

    [Fact]
    public void ReapAbandonedSessions_ExpiredSessions_Deletes()
    {
        var session = Assert.IsType<Result<StagedSession>.Success>(
            stager.CreateSession("test-bucket", "old.txt", "text/plain", new Dictionary<string, string>())).Value;

        var reaped = stager.ReapAbandonedSessions(DateTime.UtcNow + TimeSpan.FromMinutes(5));
        Assert.Equal(1, reaped);

        Assert.IsType<Result<StagedSession>.Failure>(stager.GetSession(session.SessionId));
    }
}
