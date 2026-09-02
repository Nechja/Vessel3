using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public sealed class BatchDeleteTests : IDisposable
{
    private readonly string root;
    private readonly IFileSync sync = new PortableFileSync();
    private readonly IDurableWrite durable = new DurableWrite(new PortableFileSync());
    private readonly Bucket bucket;

    public BatchDeleteTests()
    {
        root = Path.Combine(Path.GetTempPath(), "vessel3-batchdel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bucket = new Bucket("batch", root, sync, durable);
        bucket.Open();
    }

    public void Dispose()
    {
        bucket.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static PutRequest Req(string body) => new(
        BlobSha: "deadbeef" + body.GetHashCode(StringComparison.Ordinal).ToString("x8", System.Globalization.CultureInfo.InvariantCulture),
        Md5: "00",
        Size: body.Length,
        ContentType: "text/plain",
        Metadata: new Dictionary<string, string>());

    private int LogRecordCount()
    {
        var bytes = File.ReadAllBytes(Path.Combine(root, "log"));
        var count = 0;
        foreach (var b in bytes) if (b == (byte)'\n') count++;
        return count;
    }

    private static DeleteOutcome Ok(Result<DeleteOutcome> r) =>
        Assert.IsType<Result<DeleteOutcome>.Success>(r).Value;

    [Fact]
    public void Unversioned_batch_appends_one_record()
    {
        bucket.AppendPut("a", Req("a"));
        bucket.AppendPut("b", Req("b"));
        bucket.AppendPut("c", Req("c"));
        var recordsBefore = LogRecordCount();

        var results = bucket.AppendDeleteBatch([
            new BatchDeleteItem("a", null, false),
            new BatchDeleteItem("b", null, false),
            new BatchDeleteItem("c", null, false),
        ]);

        Assert.All(results, r => Assert.True(Ok(r).Found));
        Assert.Equal(recordsBefore + 1, LogRecordCount());
        Assert.True(bucket.Index.IsEmpty());
    }

    [Fact]
    public void Versioned_batch_creates_distinct_markers()
    {
        bucket.SetVersioning(VersioningStatus.Enabled);
        bucket.AppendPut("a", Req("a"));
        bucket.AppendPut("b", Req("b"));

        var results = bucket.AppendDeleteBatch([
            new BatchDeleteItem("a", null, false),
            new BatchDeleteItem("b", null, false),
        ]);

        var first = Ok(results[0]);
        var second = Ok(results[1]);
        Assert.True(first.IsDeleteMarker);
        Assert.True(second.IsDeleteMarker);
        Assert.NotEqual(first.VersionId, second.VersionId);
        Assert.Equal(VersionKind.DeleteMarker, bucket.Index.GetCurrentKind("a"));
        Assert.Equal(VersionKind.DeleteMarker, bucket.Index.GetCurrentKind("b"));
    }

    [Fact]
    public void Suspended_batch_reuses_null_version()
    {
        bucket.SetVersioning(VersioningStatus.Suspended);
        bucket.AppendPut("a", Req("a"));

        var results = bucket.AppendDeleteBatch([new BatchDeleteItem("a", null, false)]);

        var outcome = Ok(results[0]);
        Assert.True(outcome.IsDeleteMarker);
        Assert.Equal("null", outcome.VersionId);
        Assert.Equal(1, bucket.Index.CountVersions("a"));
    }

    [Fact]
    public void Retention_blocked_key_does_not_fail_batch()
    {
        bucket.SetVersioning(VersioningStatus.Enabled);
        var held = bucket.AppendPut("held", Req("h") with
        {
            Retention = new Retention(RetentionMode.Compliance, DateTimeOffset.UtcNow.AddDays(1)),
        });
        var free = bucket.AppendPut("free", Req("f"));

        var results = bucket.AppendDeleteBatch([
            new BatchDeleteItem("held", held.VersionId, false),
            new BatchDeleteItem("free", free.VersionId, false),
        ]);

        var blocked = Assert.IsType<Result<DeleteOutcome>.Failure>(results[0]);
        Assert.IsType<AccessDeniedError>(blocked.Error);
        Assert.True(Ok(results[1]).Found);
        Assert.NotNull(Assert.IsType<Result<PutEntry?>.Success>(bucket.Index.GetVersion("held", held.VersionId)).Value);
        Assert.Null(Assert.IsType<Result<PutEntry?>.Success>(bucket.Index.GetVersion("free", free.VersionId)).Value);
    }

    [Fact]
    public void Duplicate_keys_evaluate_sequentially()
    {
        bucket.AppendPut("a", Req("a"));

        var results = bucket.AppendDeleteBatch([
            new BatchDeleteItem("a", null, false),
            new BatchDeleteItem("a", null, false),
        ]);

        Assert.True(Ok(results[0]).Found);
        Assert.False(Ok(results[1]).Found);
    }

    [Fact]
    public void Missing_key_reports_not_found_outcome()
    {
        var results = bucket.AppendDeleteBatch([new BatchDeleteItem("ghost", null, false)]);
        var outcome = Ok(results[0]);
        Assert.False(outcome.Found);
        Assert.False(outcome.IsDeleteMarker);
    }

    [Fact]
    public void Batch_survives_replay()
    {
        bucket.AppendPut("a", Req("a"));
        bucket.AppendPut("b", Req("b"));
        bucket.AppendDeleteBatch([
            new BatchDeleteItem("a", null, false),
            new BatchDeleteItem("b", null, false),
        ]);
        bucket.Dispose();

        File.Delete(Path.Combine(root, "index.db"));
        var reopened = new Bucket("batch", root, sync, durable);
        reopened.Open();
        Assert.True(reopened.Index.IsEmpty());
        reopened.Dispose();
    }

    [Fact]
    public void Missing_bucket_fails_whole_batch()
    {
        var regRoot = Path.Combine(Path.GetTempPath(), "vessel3-batchreg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(regRoot);
        using var registry = new BucketRegistry(new BucketRegistryOptions(regRoot), sync, durable);
        try
        {
            var result = registry.DeleteBatch("nope", [new BatchDeleteItem("k", null, false)]);
            var failure = Assert.IsType<Result<IReadOnlyList<Result<DeleteOutcome>>>.Failure>(result);
            Assert.IsType<NoSuchBucketError>(failure.Error);
        }
        finally
        {
            try { Directory.Delete(regRoot, recursive: true); } catch { }
        }
    }
}
