using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Azure.Auth;
using Xunit;

namespace Vessel3.Tests;

public class ReliabilityRegressionTests : IDisposable
{
    private readonly string root;

    public ReliabilityRegressionTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-reliability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public void HotIndexCache_Overwrites_DoNotGrowEvictionListUnbounded()
    {
        var cache = new HotIndexCache(maxCapacity: 100);
        var dummy = new PutEntry("v1", DateTimeOffset.UtcNow, "sha", "md5", 100, "text/plain", new Dictionary<string, string>());

        for (var i = 0; i < 100_000; i++)
        {
            cache.Set("test-key", dummy);
        }

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void HotIndexCache_TryFill_RejectsStaleReadWhenVersionChanged()
    {
        var cache = new HotIndexCache(maxCapacity: 100);
        var staleEntry = new PutEntry("v1", DateTimeOffset.UtcNow, "sha1", "md5", 10, "text/plain", new Dictionary<string, string>());
        var newEntry = new PutEntry("v2", DateTimeOffset.UtcNow, "sha2", "md5", 20, "text/plain", new Dictionary<string, string>());

        var initialVersion = cache.GetVersion("foo");
        Assert.Equal(0, initialVersion);

        cache.Set("foo", newEntry);

        var filled = cache.TryFill("foo", staleEntry, expectedVersion: initialVersion);
        Assert.False(filled);

        Assert.True(cache.TryGet("foo", out var current));
        Assert.NotNull(current);
        Assert.Equal("v2", current.VersionId);
    }

    [Fact]
    public void HotIndexCache_UncachedUpdateLegalHold_BumpsVersionSequence()
    {
        var cache = new HotIndexCache(maxCapacity: 100);
        var initialVersion = cache.GetVersion("uncached-key");
        Assert.Equal(0, initialVersion);

        cache.UpdateLegalHold("uncached-key", "v1", true);

        var bumpedVersion = cache.GetVersion("uncached-key");
        Assert.True(bumpedVersion > initialVersion);

        var staleEntry = new PutEntry("v1", DateTimeOffset.UtcNow, "sha", "md5", 10, "text/plain", new Dictionary<string, string>());
        var filled = cache.TryFill("uncached-key", staleEntry, expectedVersion: initialVersion);
        Assert.False(filled);
    }

    [Fact]
    public void BucketIndex_UncommittedTransaction_DoesNotMutateHotCache()
    {
        var dbPath = Path.Combine(root, "uncommitted-test-index.db");
        using var index = new BucketIndex(dbPath);
        index.Open();

        var entry = new PutEvent(
            1, DateTimeOffset.UtcNow, "uncommitted-key", "v1", "blobsha1", "md5", 10, "text/plain",
            new Dictionary<string, string>());

        using (var tx = index.BeginTransaction())
        {
            index.Insert(entry);
            Assert.False(index.HotCache.TryGet("uncommitted-key", out _));
        }

        Assert.False(index.HotCache.TryGet("uncommitted-key", out _));
    }

    [Fact]
    public void BucketIndex_CommittedTransaction_MutatesHotCache()
    {
        var dbPath = Path.Combine(root, "committed-test-index.db");
        using var index = new BucketIndex(dbPath);
        index.Open();

        var entry = new PutEvent(
            1, DateTimeOffset.UtcNow, "committed-key", "v1", "blobsha1", "md5", 10, "text/plain",
            new Dictionary<string, string>());

        using (var tx = index.BeginTransaction())
        {
            index.Insert(entry);
            Assert.False(index.HotCache.TryGet("committed-key", out _));
            tx.Commit();
        }

        Assert.True(index.HotCache.TryGet("committed-key", out var cached));
        Assert.NotNull(cached);
        Assert.Equal("v1", cached.VersionId);
    }

    [Fact]
    public void BucketIndex_MissingKey_ReturnsNoSuchKey()
    {
        var dbPath = Path.Combine(root, "missing-key-index.db");
        using var index = new BucketIndex(dbPath);
        index.Open();

        var result = index.GetVersion("nonexistent-key", "some-version");
        Assert.True(result.TryGetError(out var err));
        Assert.IsType<NoSuchKeyError>(err);
    }

    [Fact]
    public void BucketIndex_MissingVersionOnExistingKey_ReturnsNoSuchVersion()
    {
        var dbPath = Path.Combine(root, "missing-version-index.db");
        using var index = new BucketIndex(dbPath);
        index.Open();

        var entry = new PutEvent(
            1, DateTimeOffset.UtcNow, "existing-key", "v1", "blobsha1", "md5", 10, "text/plain",
            new Dictionary<string, string>());

        using (var tx = index.BeginTransaction())
        {
            index.Insert(entry);
            tx.Commit();
        }

        var result = index.GetVersion("existing-key", "nonexistent-version");
        Assert.True(result.TryGetError(out var err));
        Assert.IsType<NoSuchVersionError>(err);
    }

    [Fact]
    public void AzureSharedKeyVerifier_MultiByteSignature_ReturnsAuthenticationErrorWithoutException()
    {
        var verifier = new AzureSharedKeyVerifier("myaccount", "dGVzdC1zZWNyZXQta2V5LTEyMzQ1Njc4OTAxMjM0NTY=", identity: null);
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/mycontainer";
        ctx.Request.Headers["x-ms-date"] = DateTimeOffset.UtcNow.ToString("R");
        ctx.Request.Headers["x-ms-version"] = "2024-05-04";

        var multiByteChars = string.Concat(Enumerable.Repeat("🚀🌟🔥💥", 15));
        ctx.Request.Headers.Authorization = $"SharedKey myaccount:{multiByteChars}";

        var result = verifier.Verify(ctx.Request, pathAccount: null);
        Assert.True(result.TryGetError(out var err));
        Assert.IsType<AzureAuthenticationError>(err);
    }

    [Fact]
    public async Task BucketIndex_ReferencedBlobs_ThrowsOperationCanceledException()
    {
        var dbPath = Path.Combine(root, "test-index.db");
        using var index = new BucketIndex(dbPath);
        index.Open();

        var entry = new PutEvent(
            1, DateTimeOffset.UtcNow, "key1", "v1", "blobsha1", "md5", 10, "text/plain",
            new Dictionary<string, string>());
        index.Insert(entry);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in index.ReferencedBlobs(cts.Token))
            {
            }
        });
    }
}
