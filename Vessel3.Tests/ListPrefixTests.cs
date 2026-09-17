using Microsoft.Data.Sqlite;
using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public sealed class ListPrefixTests : IDisposable
{
    private readonly string root;
    private readonly BucketRegistry registry;

    public ListPrefixTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-listprefix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        registry = new BucketRegistry(new BucketRegistryOptions(root), new PortableFileSync(), new DurableWrite(new PortableFileSync()));
        Assert.IsType<Result<bool>.Success>(registry.Create("bucket"));
    }

    public void Dispose()
    {
        registry.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private void Seed(params string[] keys)
    {
        var index = registry.OpenBuckets().Single().Index;
        using var tx = index.BeginTransaction();
        var seq = index.MaxSeq();
        foreach (var key in keys)
        {
            seq++;
            new PutEvent(seq, DateTimeOffset.UnixEpoch, key, "v" + seq,
                "sha", "00", 1, "application/octet-stream", new Dictionary<string, string>(), null).ApplyTo(index);
        }
        index.MarkApplied(seq);
        tx.Commit();
    }

    private string[] Current(string? prefix, int limit = 1000, KeyBound? from = null) =>
        Assert.IsType<Result<CurrentPage>.Success>(registry.ListCurrent("bucket", prefix, from, limit)).Value.Entries.Select(e => e.Key).ToArray();

    private string[] Versions(string? prefix, string? keyMarker = null, int limit = 1000) =>
        Assert.IsType<Result<VersionsPage>.Success>(registry.ListAllVersions("bucket", prefix, keyMarker, limit)).Value.Entries.Select(e => e.Key).ToArray();

    private ListPage List(string? prefix, int maxKeys, string? token = null) =>
        Assert.IsType<Result<ListPage>.Success>(new BucketLister(registry).List(
            new ListRequest("bucket", prefix, null, null, maxKeys), token)).Value;

    [Fact]
    public void Prefix_Is_Case_Sensitive()
    {
        Seed("Chunks/a", "chunks/b", "CHUNKS/c");
        Assert.Equal(new[] { "chunks/b" }, Current("chunks/"));
        Assert.Equal(new[] { "Chunks/a" }, Current("Chunks/"));
        Assert.Equal(new[] { "chunks/b" }, Versions("chunks/"));
        Assert.Equal(new[] { "CHUNKS/c" }, Versions("CHUNKS/"));
    }

    [Fact]
    public void Prefix_Wildcard_Characters_Are_Literal()
    {
        Seed("a%b", "axb", "a_b", "a\\b", "a\\%");
        Assert.Equal(new[] { "a%b" }, Current("a%"));
        Assert.Equal(new[] { "a_b" }, Current("a_"));
        Assert.Equal(new[] { "a\\%", "a\\b" }, Current("a\\"));
        Assert.Equal(new[] { "a%b" }, Versions("a%"));
    }

    [Fact]
    public void Prefix_NonAscii()
    {
        Seed("ü/1", "ü/2", "ý/1", "v/1", "😀/1", "😀/2", "😁/1", "/1");
        Assert.Equal(new[] { "ü/1", "ü/2" }, Current("ü/"));
        Assert.Equal(new[] { "😀/1", "😀/2" }, Current("😀/"));
        Assert.Equal(new[] { "😀/1", "😀/2" }, Current("😀"));
        Assert.Equal(new[] { "/1" }, Current(""));
    }

    [Fact]
    public void Prefix_Ending_Before_Surrogate_Gap()
    {
        Seed("a퟿x", "a", "a퟿");
        Assert.Equal(new[] { "a퟿", "a퟿x" }, Current("a퟿"));
    }

    [Fact]
    public void Prefix_Ending_In_Max_Code_Point()
    {
        Seed("a\U0010FFFFx", "a\U0010FFFF", "b", "a");
        Assert.Equal(new[] { "a\U0010FFFF", "a\U0010FFFFx" }, Current("a\U0010FFFF"));
        Assert.Equal(new[] { "a\U0010FFFF", "a\U0010FFFFx" }, Versions("a\U0010FFFF"));
    }

    [Fact]
    public void Prefix_Pages_Do_Not_Leak_Neighbours()
    {
        Seed(Enumerable.Range(0, 25).Select(i => $"p/{i:D3}").Concat(Enumerable.Range(0, 5).Select(i => $"q/{i:D3}")).Concat(["o/999", "p", "p0"]).ToArray());

        var all = new List<string>();
        string? token = null;
        var pages = 0;
        do
        {
            var page = List("p/", 10, token);
            pages++;
            all.AddRange(page.Entries.Select(e => e.Key));
            token = page.NextContinuationToken;
        } while (token is not null);

        Assert.Equal(3, pages);
        Assert.Equal(Enumerable.Range(0, 25).Select(i => $"p/{i:D3}"), all);
    }

    [Fact]
    public void Prefix_With_Continuation_Bound_Uses_Tighter_Lower_Bound()
    {
        Seed("p/1", "p/2", "p/3", "q/1");
        Assert.Equal(new[] { "p/3" }, Current("p/", from: KeyBound.After("p/2")));
        Assert.Equal(new[] { "p/2", "p/3" }, Current("p/", from: KeyBound.From("p/2")));
        Assert.Equal(new[] { "p/1", "p/2", "p/3" }, Current("p/", from: KeyBound.From("a")));
        Assert.Equal(new[] { "p/2", "p/3" }, Versions("p/", keyMarker: "p/1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prefix_Query_Seeks_The_Key_Index(bool continued)
    {
        Seed("a");
        using var conn = new SqliteConnection($"Data Source={Path.Combine(root, "buckets", "bucket", "index.db")};Mode=ReadOnly");
        conn.Open();
        var queries = new[]
        {
            BucketIndex.ListCurrentSql("p/", "p0", continued ? KeyBound.After("p/1") : null),
            BucketIndex.ListAllVersionsSql("p/", "p0", continued ? "p/1" : null),
        };
        foreach (var sql in queries)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var name in new[] { "$kp", "$lo", "$hi", "$from", "$km", "$lim" }) cmd.Parameters.AddWithValue(name, "x");
            using var r = cmd.ExecuteReader();
            var plan = new List<string>();
            while (r.Read()) plan.Add(r.GetString(3));
            Assert.Contains(plan, line => line.StartsWith("SEARCH v1 USING INDEX idx_key_seq (key>", StringComparison.Ordinal) || line.StartsWith("SEARCH versions USING INDEX idx_key_seq (key>", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, line => line.StartsWith("SCAN", StringComparison.Ordinal));
        }
    }
}

public class KeyRangeTests
{
    [Theory]
    [InlineData("abc", "abd")]
    [InlineData("a/", "a0")]
    [InlineData("aü", "aý")]
    [InlineData("a😀", "a😁")]
    [InlineData("a퟿", "a")]
    [InlineData("a\U0010FFFF", "b")]
    [InlineData("a\U0010FFFF\U0010FFFF", "b")]
    public void Successor_Is_Next_Key_After_All_Prefixed_Keys(string prefix, string expected) =>
        Assert.Equal(expected, KeyRange.Successor(prefix));

    [Theory]
    [InlineData("")]
    [InlineData("\U0010FFFF")]
    [InlineData("\U0010FFFF\U0010FFFF")]
    public void Successor_Is_Null_When_Nothing_Follows(string prefix) =>
        Assert.Null(KeyRange.Successor(prefix));

    [Theory]
    [InlineData("abc")]
    [InlineData("a/")]
    [InlineData("aü")]
    [InlineData("a😀")]
    [InlineData("a퟿")]
    [InlineData("a\U0010FFFF")]
    public void Successor_Orders_Correctly_In_Utf8(string prefix)
    {
        var hi = KeyRange.Successor(prefix)!;
        static int Cmp(string a, string b) => System.Text.Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(System.Text.Encoding.UTF8.GetBytes(b));
        Assert.True(Cmp(prefix, hi) < 0);
        foreach (var tail in new[] { "", "\0", "z", "\U0010FFFF", "￿", "😀" })
            Assert.True(Cmp(prefix + tail, hi) < 0, $"{prefix + tail} should sort before {hi}");
    }
}
