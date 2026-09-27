using Vessel3.Primitives;
using Vessel3.Storage;
using Xunit;

namespace Vessel3.Tests;

public class BucketOwnershipTests : IDisposable
{
    private readonly string root;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly List<IDisposable> disposables = [];

    public BucketOwnershipTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-bucket-ownership-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        foreach (var d in disposables) d.Dispose();
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private BucketRegistry Registry()
    {
        var reg = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        disposables.Add(reg);
        return reg;
    }

    [Fact]
    public void Bucket_Created_With_Owner_Persists_And_Can_Be_Retrieved()
    {
        var reg = Registry();
        var createResult = reg.Create("alice-bucket", "usr_alice123");
        Assert.IsType<Result<bool>.Success>(createResult);

        var ownerResult = reg.GetOwner("alice-bucket");
        Assert.True(ownerResult.TryGetValue(out var owner, out _));
        Assert.Equal("usr_alice123", owner);

        using var reg2 = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        var ownerResult2 = reg2.GetOwner("alice-bucket");
        Assert.True(ownerResult2.TryGetValue(out var owner2, out _));
        Assert.Equal("usr_alice123", owner2);
    }

    [Fact]
    public void Bucket_Created_Without_Owner_Has_Null_Owner()
    {
        var reg = Registry();
        reg.Create("orphan-bucket");

        var ownerResult = reg.GetOwner("orphan-bucket");
        Assert.True(ownerResult.TryGetValue(out var owner, out _));
        Assert.Null(owner);
    }

    [Fact]
    public void SetOwner_Updates_And_Persists_Ownership()
    {
        var reg = Registry();
        reg.Create("team-bucket", "usr_alice");

        var updateResult = reg.SetOwner("team-bucket", "usr_bob");
        Assert.Same(Result.Ok, updateResult);

        var ownerResult = reg.GetOwner("team-bucket");
        Assert.True(ownerResult.TryGetValue(out var owner, out _));
        Assert.Equal("usr_bob", owner);

        using var reg2 = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        var reloadedOwner = reg2.GetOwner("team-bucket");
        Assert.True(reloadedOwner.TryGetValue(out var ownerReloaded, out _));
        Assert.Equal("usr_bob", ownerReloaded);

        var emptyOwnerResult = reg.SetOwner("team-bucket", "");
        Assert.True(emptyOwnerResult.Match(() => false, err => err is InvalidArgumentError));

        var missingBucketResult = reg.SetOwner("nonexistent", "usr_someone");
        Assert.True(missingBucketResult.Match(() => false, err => err is NoSuchBucketError));
    }

    [Fact]
    public void List_Filters_By_OwnerId_Correctly()
    {
        var reg = Registry();
        reg.Create("bucket-alice-1", "usr_alice");
        reg.Create("bucket-alice-2", "usr_alice");
        reg.Create("bucket-bob-1", "usr_bob");
        reg.Create("bucket-unowned");

        var allBuckets = reg.List().ToList();
        Assert.Equal(4, allBuckets.Count);

        var aliceBuckets = reg.List("usr_alice").ToList();
        Assert.Equal(2, aliceBuckets.Count);
        Assert.All(aliceBuckets, b => Assert.Equal("usr_alice", b.OwnerId));
        Assert.Contains(aliceBuckets, b => b.Name == "bucket-alice-1");
        Assert.Contains(aliceBuckets, b => b.Name == "bucket-alice-2");

        var bobBuckets = reg.List("usr_bob").ToList();
        Assert.Single(bobBuckets);
        Assert.Equal("bucket-bob-1", bobBuckets[0].Name);
        Assert.Equal("usr_bob", bobBuckets[0].OwnerId);

        var charlieBuckets = reg.List("usr_charlie").ToList();
        Assert.Empty(charlieBuckets);
    }
}
