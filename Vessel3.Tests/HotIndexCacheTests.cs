using System.Collections.Frozen;
using Xunit;

namespace Vessel3.Tests;

public class HotIndexCacheTests
{
    private static PutEntry MakeEntry(string vid, string key = "k1") => new(
        VersionId: vid,
        At: DateTimeOffset.UtcNow,
        BlobSha: "sha256",
        Md5: "md5",
        Size: 1024,
        ContentType: "application/octet-stream",
        Metadata: FrozenDictionary<string, string>.Empty,
        Parts: null,
        Tags: FrozenDictionary<string, string>.Empty);

    [Fact]
    public void SetAndGet_ReturnsCachedEntry()
    {
        var cache = new HotIndexCache(100);
        var entry = MakeEntry("v1");

        cache.Set("k1", entry);

        Assert.True(cache.TryGet("k1", out var got));
        Assert.NotNull(got);
        Assert.Equal("v1", got.VersionId);
    }

    [Fact]
    public void SetNull_ReturnsNegativeCacheHit()
    {
        var cache = new HotIndexCache(100);

        cache.Set("deleted-key", null);

        Assert.True(cache.TryGet("deleted-key", out var got));
        Assert.Null(got);
    }

    [Fact]
    public void MissingKey_ReturnsCacheMiss()
    {
        var cache = new HotIndexCache(100);

        Assert.False(cache.TryGet("non-existent", out var got));
        Assert.Null(got);
    }

    [Fact]
    public void Evict_RemovesKeyFromCache()
    {
        var cache = new HotIndexCache(100);
        cache.Set("k1", MakeEntry("v1"));

        cache.Evict("k1");

        Assert.False(cache.TryGet("k1", out _));
    }

    [Fact]
    public void UpdateTags_MatchingVersion_UpdatesTagsInCache()
    {
        var cache = new HotIndexCache(100);
        cache.Set("k1", MakeEntry("v1"));

        var tags = new Dictionary<string, string> { ["env"] = "prod" };
        cache.UpdateTags("k1", "v1", tags);

        Assert.True(cache.TryGet("k1", out var got));
        Assert.NotNull(got);
        Assert.NotNull(got.Tags);
        Assert.Equal("prod", got.Tags["env"]);
    }

    [Fact]
    public void UpdateTags_MismatchedVersion_DoesNotUpdateCache()
    {
        var cache = new HotIndexCache(100);
        cache.Set("k1", MakeEntry("v1"));

        var tags = new Dictionary<string, string> { ["env"] = "prod" };
        cache.UpdateTags("k1", "different-version", tags);

        Assert.True(cache.TryGet("k1", out var got));
        Assert.NotNull(got);
        Assert.NotNull(got.Tags);
        Assert.Empty(got.Tags);
    }

    [Fact]
    public void CapacityTrim_EvictsOldestEntriesOnOverflow()
    {
        var cache = new HotIndexCache(maxCapacity: 3);

        cache.Set("k1", MakeEntry("v1"));
        cache.Set("k2", MakeEntry("v2"));
        cache.Set("k3", MakeEntry("v3"));
        cache.Set("k4", MakeEntry("v4"));

        Assert.False(cache.TryGet("k1", out _));
        Assert.True(cache.TryGet("k4", out _));
    }
}
