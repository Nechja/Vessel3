using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class BucketLifecycleTests : IDisposable
{
    private readonly string root;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly List<IDisposable> disposables = [];

    public BucketLifecycleTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        foreach (var d in disposables) d.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private BucketRegistry Registry()
    {
        var reg = new BucketRegistry(new BucketRegistryOptions(root), sync, durable);
        disposables.Add(reg);
        return reg;
    }

    private static PutRequest Req(string body) => new(
        BlobSha: Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))),
        Md5: Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(body))),
        Size: body.Length, ContentType: "text/plain", Metadata: new Dictionary<string, string>());

    [Fact]
    public void CloseIfEmpty_Seals_Empty_Bucket_Against_Writes()
    {
        using var b = new Bucket("b", Path.Combine(root, "b"), sync, durable);
        b.Open();

        Assert.True(b.CloseIfEmpty());
        Assert.Throws<InvalidOperationException>(() => b.AppendPut("k", Req("x")));
        Assert.IsType<Result<DeleteOutcome>.Failure>(b.AppendDelete("k", false));
    }

    [Fact]
    public void CloseIfEmpty_Refuses_A_NonEmpty_Bucket_Which_Stays_Writable()
    {
        using var b = new Bucket("b", Path.Combine(root, "b"), sync, durable);
        b.Open();
        Assert.NotNull(b.AppendPut("k", Req("x")));

        Assert.False(b.CloseIfEmpty());
        Assert.NotNull(b.AppendPut("k2", Req("y")));
    }

    [Fact]
    public void Delete_Of_A_NonEmpty_Bucket_Fails()
    {
        var reg = Registry();
        reg.Create("mybucket");
        reg.AppendPut("mybucket", "k", Req("x"));

        Assert.True(reg.Delete("mybucket").TryGetError(out var err));
        Assert.IsType<BucketNotEmptyError>(err);
    }

    [Fact]
    public void Create_Delete_Recreate_Same_Name_Leaves_No_Zombie()
    {
        var reg = Registry();
        Assert.IsType<Result<bool>.Success>(reg.Create("mybucket"));
        Assert.False(reg.Delete("mybucket").TryGetError(out _));
        Assert.IsType<Result<bool>.Success>(reg.Create("mybucket"));

        // A write to the recreated bucket must land in the live bucket and read back.
        Assert.IsType<Result<PutEntry>.Success>(reg.AppendPut("mybucket", "k", Req("hello")));
        var cur = ((Result<PutEntry?>.Success)reg.GetCurrentPut("mybucket", "k")).Value!;
        Assert.Equal("hello".Length, cur.Size);
    }

    [Fact]
    public void Access_After_Delete_Reports_NoSuchBucket()
    {
        var reg = Registry();
        reg.Create("mybucket");
        Assert.False(reg.Delete("mybucket").TryGetError(out _));

        Assert.True(reg.GetVersioning("mybucket") is Result<VersioningStatus>.Failure { Error: NoSuchBucketError });
    }
}
