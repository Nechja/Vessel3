using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class BucketListerTests
{
    private sealed class StubRegistry : IBucketRegistry
    {
        private readonly List<VersionListEntry> entries;
        public StubRegistry(IEnumerable<VersionListEntry> e) => entries = e.ToList();

        public Result<List<VersionListEntry>> ListCurrent(string bucket, string? prefix, string? startAfter)
        {
            IEnumerable<VersionListEntry> q = entries;
            if (prefix is not null) q = q.Where(v => v.Key.StartsWith(prefix, StringComparison.Ordinal));
            if (startAfter is not null) q = q.Where(v => StringComparer.Ordinal.Compare(v.Key, startAfter) > 0);
            return q.OrderBy(v => v.Key, StringComparer.Ordinal).ToList();
        }

        public bool IsValidName(string bucket) => true;
        public Result<bool> Create(string bucket) => true;
        public Result Delete(string bucket) => Result.Ok;
        public Result<bool> Exists(string bucket) => true;
        public IEnumerable<BucketInfo> List() => [];
        public Result<PutEntry?> GetCurrentPut(string bucket, string key) => (PutEntry?)null;
        public Result<PutEntry?> GetVersion(string bucket, string key, string versionId) => (PutEntry?)null;
        public Result<PutEntry> AppendPut(string bucket, string key, PutRequest req) => throw new NotImplementedException();
        public Result<DeleteOutcome> AppendDelete(string bucket, string key, bool bypassGovernance) => throw new NotImplementedException();
        public Result<DeleteOutcome> HardDeleteVersion(string bucket, string key, string versionId, bool bypassGovernance) => throw new NotImplementedException();
        public Result<VersionsPage> ListAllVersions(string bucket, string? prefix, string? keyMarker, int limit) => new VersionsPage([], false);
        public Result<PutTaggingOutcome> PutTagging(string bucket, string key, string? versionId, IReadOnlyDictionary<string, string> tags) => throw new NotImplementedException();
        public int? GetCurrentKind(string bucket, string key) => null;
        public int? GetVersionKind(string bucket, string key, string versionId) => null;
        public Result<VersioningStatus> GetVersioning(string bucket) => VersioningStatus.Unversioned;
        public Result SetVersioning(string bucket, VersioningStatus status) => Result.Ok;
        public Result<ObjectLockConfig?> GetObjectLock(string bucket) => (ObjectLockConfig?)null;
        public Result SetObjectLock(string bucket, ObjectLockConfig cfg) => Result.Ok;
        public Result<LifecycleConfig?> GetLifecycle(string bucket) => (LifecycleConfig?)null;
        public Result SetLifecycle(string bucket, LifecycleConfig cfg) => Result.Ok;
        public Result RemoveLifecycle(string bucket) => Result.Ok;
        public IEnumerable<Bucket> OpenBuckets() => [];
        public Result PutRetention(string bucket, string key, string versionId, Retention retention, bool bypassGovernance) => throw new NotImplementedException();
        public Result<Retention?> GetRetention(string bucket, string key, string versionId) => (Retention?)null;
        public Result PutLegalHold(string bucket, string key, string versionId, bool on) => throw new NotImplementedException();
        public Result<bool> GetLegalHold(string bucket, string key, string versionId) => false;
        public IEnumerable<string> AllReferencedBlobs() => [];
        public void Dispose() { }
    }

    private static VersionListEntry V(string key) => new(
        Key: key, VersionId: "v1", At: DateTimeOffset.UtcNow,
        BlobSha: "", Md5: "00", Size: 1, ContentType: "application/octet-stream",
        Metadata: new Dictionary<string, string>());

    [Fact]
    public void Lists_AllUnderLimit()
    {
        var reg = new StubRegistry(new[] { V("a"), V("b"), V("c") });
        var page = AssertSuccess(new BucketLister(reg).List(
            new ListRequest("b1", null, null, null, 10), continuationToken: null));
        Assert.False(page.IsTruncated);
        Assert.Equal(3, page.Entries.Count);
    }

    [Fact]
    public void Truncates_At_MaxKeys()
    {
        var reg = new StubRegistry(new[] { V("a"), V("b"), V("c"), V("d") });
        var page = AssertSuccess(new BucketLister(reg).List(
            new ListRequest("b1", null, null, null, 2), continuationToken: null));
        Assert.True(page.IsTruncated);
        Assert.Equal(2, page.Entries.Count);
        Assert.NotNull(page.NextContinuationToken);
    }

    [Fact]
    public void Delimiter_FoldsCommonPrefixes()
    {
        var reg = new StubRegistry(new[] { V("a/1"), V("a/2"), V("a/3"), V("b/1"), V("c") });
        var page = AssertSuccess(new BucketLister(reg).List(
            new ListRequest("b1", null, "/", null, 10), continuationToken: null));
        var prefixes = page.Entries.OfType<ListEntry.CommonPrefix>().Select(p => p.Key).ToList();
        var contents = page.Entries.OfType<ListEntry.Contents>().Select(c => c.Key).ToList();
        Assert.Equal(new[] { "a/", "b/" }, prefixes);
        Assert.Equal(new[] { "c" }, contents);
    }

    [Fact]
    public void Delimiter_DedupesAdjacentPrefix()
    {
        var reg = new StubRegistry(new[] { V("p/x"), V("p/y"), V("p/z") });
        var page = AssertSuccess(new BucketLister(reg).List(
            new ListRequest("b1", null, "/", null, 10), continuationToken: null));
        var prefixes = page.Entries.OfType<ListEntry.CommonPrefix>().Select(p => p.Key).ToList();
        Assert.Equal(new[] { "p/" }, prefixes);
    }

    [Fact]
    public void Prefix_TrimsFromDelimiterFolding()
    {
        var reg = new StubRegistry(new[] { V("users/alice/a"), V("users/bob/b") });
        var page = AssertSuccess(new BucketLister(reg).List(
            new ListRequest("b1", "users/", "/", null, 10), continuationToken: null));
        var prefixes = page.Entries.OfType<ListEntry.CommonPrefix>().Select(p => p.Key).ToList();
        Assert.Equal(new[] { "users/alice/", "users/bob/" }, prefixes);
    }

    private static ListPage AssertSuccess(Result<ListPage> r) =>
        Assert.IsType<Result<ListPage>.Success>(r).Value;
}
