using Vessel3.Server;
using Xunit;

namespace Vessel3.Tests;

public sealed class ListPagingTests : IDisposable
{
    private const long PageAllocationBudget = 256 * 1024;

    private readonly string root;
    private readonly BucketRegistry registry;

    public ListPagingTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-listpaging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        registry = new BucketRegistry(new BucketRegistryOptions(root), new PortableFileSync(), new DurableWrite(new PortableFileSync()));
        Assert.IsType<Result<bool>.Success>(registry.Create("bucket"));
    }

    public void Dispose()
    {
        registry.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private void Seed(IEnumerable<string> keys, IReadOnlyList<MultipartPart>? parts = null)
    {
        var index = registry.OpenBuckets().Single().Index;
        using var tx = index.BeginTransaction();
        var seq = index.MaxSeq();
        foreach (var key in keys)
        {
            seq++;
            new PutEvent(seq, DateTimeOffset.UnixEpoch, key, "v" + seq,
                "sha", "00", 1, "application/octet-stream", new Dictionary<string, string>(), parts).ApplyTo(index);
        }
        index.MarkApplied(seq);
        tx.Commit();
    }

    private static IEnumerable<string> Numbered(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i => prefix + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture));

    private ListPage List(string? prefix, string? delimiter, int maxKeys, string? token = null, string? startAfter = null) =>
        Assert.IsType<Result<ListPage>.Success>(new BucketLister(registry).List(
            new ListRequest("bucket", prefix, delimiter, startAfter, maxKeys), token)).Value;

    private static (ListPage Page, long Bytes) Allocated(Func<ListPage> list)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var page = list();
        return (page, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static string[] Keys(ListPage page) => page.Entries.Select(e => e.Key).ToArray();

    [Fact]
    public void Page_Work_Is_Bounded_By_MaxKeys()
    {
        Seed(Numbered("k", 20_000));
        List(null, null, 10);

        var (page, bytes) = Allocated(() => List(null, null, 10));

        Assert.Equal(10, page.Entries.Count);
        Assert.True(page.IsTruncated);
        Assert.True(bytes < PageAllocationBudget, $"listing 10 of 20000 keys allocated {bytes} bytes");
    }

    [Fact]
    public void Delimiter_Page_Work_Is_Bounded_By_MaxKeys()
    {
        Seed(Numbered("dir/", 20_000).Append("tail"));
        List(null, "/", 10);

        var (page, bytes) = Allocated(() => List(null, "/", 10));

        Assert.Equal(new[] { "dir/", "tail" }, Keys(page));
        Assert.False(page.IsTruncated);
        Assert.True(bytes < PageAllocationBudget, $"folding 20000 keys into one prefix allocated {bytes} bytes");
    }

    [Fact]
    public void Continuation_Resumes_Across_CommonPrefix()
    {
        Seed(Numbered("a/", 300).Append("b").Concat(Numbered("c/", 3)).Append("d"));

        var first = List(null, "/", 2);
        Assert.Equal(new[] { "a/", "b" }, Keys(first));
        Assert.True(first.IsTruncated);

        var second = List(null, "/", 2, first.NextContinuationToken);
        Assert.Equal(new[] { "c/", "d" }, Keys(second));
        Assert.False(second.IsTruncated);
        Assert.Null(second.NextContinuationToken);
    }

    [Fact]
    public void Continuation_After_Trailing_CommonPrefix_Does_Not_Repeat_It()
    {
        Seed(Numbered("a/", 50).Append("b"));

        var first = List(null, "/", 1);
        Assert.Equal(new[] { "a/" }, Keys(first));
        Assert.True(first.IsTruncated);
        Assert.Equal("a/", first.LastKey);

        var second = List(null, "/", 1, first.NextContinuationToken);
        Assert.Equal(new[] { "b" }, Keys(second));
        Assert.False(second.IsTruncated);
    }

    [Fact]
    public void Not_Truncated_When_Page_Ends_At_Last_Key()
    {
        Seed(["a", "b"]);
        var page = List(null, null, 2);
        Assert.Equal(new[] { "a", "b" }, Keys(page));
        Assert.False(page.IsTruncated);
        Assert.Null(page.NextContinuationToken);
    }

    [Fact]
    public void Not_Truncated_When_Rest_Folds_Into_Last_CommonPrefix()
    {
        Seed(Numbered("a/", 50));
        var page = List(null, "/", 1);
        Assert.Equal(new[] { "a/" }, Keys(page));
        Assert.False(page.IsTruncated);
    }

    [Fact]
    public void Prefix_Equal_Key_Is_Contents_And_Resumes_After_It()
    {
        Seed(["a/", "a/x", "a/y/1"]);

        var first = List("a/", "/", 1);
        Assert.Equal(new[] { "a/" }, Keys(first));
        Assert.IsType<ListEntry.Contents>(first.Entries[0]);
        Assert.True(first.IsTruncated);

        var second = List("a/", "/", 5, first.NextContinuationToken);
        Assert.Equal(new[] { "a/x", "a/y/" }, Keys(second));
        Assert.False(second.IsTruncated);
    }

    [Fact]
    public void StartAfter_CommonPrefix_Skips_Its_Subtree()
    {
        Seed(Numbered("a/", 50).Append("b"));
        var page = List(null, "/", 10, startAfter: "a/");
        Assert.Equal(new[] { "b" }, Keys(page));
        Assert.False(page.IsTruncated);
    }

    [Fact]
    public void Multipart_Etag_Carries_Part_Count()
    {
        Seed(["m"], parts: [new MultipartPart(1, "s1", "m1", 1), new MultipartPart(2, "s2", "m2", 1), new MultipartPart(3, "s3", "m3", 1)]);
        Seed(["s"]);

        var page = List(null, null, 10);
        var contents = page.Entries.OfType<ListEntry.Contents>().ToDictionary(c => c.Key, c => c.Etag);
        Assert.Equal("00-3", contents["m"]);
        Assert.Equal("00", contents["s"]);
    }
}
