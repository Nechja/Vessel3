using Vessel3.Server;
using Vessel3.Server.S3;
using Xunit;

namespace Vessel3.Tests;

public class VirtualHostResolverTests
{
    private static readonly System.Buffers.SearchValues<char> ValidBucketChars =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-.");

    private sealed class MockRegistry(HashSet<string> existingBuckets) : IBucketRegistry
    {
        public bool IsValidName(string bucket)
        {
            if (string.IsNullOrEmpty(bucket) || bucket.Length is < 3 or > 63) return false;
            if (bucket[0] is '-' or '.' || bucket[^1] is '-' or '.') return false;
            if (bucket.Contains("..", StringComparison.Ordinal)) return false;
            return !bucket.ContainsAnyExcept(ValidBucketChars);
        }

        public Result<bool> Exists(string bucket) => existingBuckets.Contains(bucket);

        public Result<bool> Create(string bucket) => true;
        public Result Delete(string bucket) => Result.Ok;
        public IEnumerable<BucketInfo> List() => [];
        public Result<PutEntry?> GetCurrentPut(string bucket, string key) => (PutEntry?)null;
        public Result<PutEntry?> GetVersion(string bucket, string key, string versionId) => (PutEntry?)null;
        public Result<PutEntry> AppendPut(string bucket, string key, PutRequest req) => throw new NotImplementedException();
        public Result<DeleteOutcome> AppendDelete(string bucket, string key, bool bypassGovernance) => throw new NotImplementedException();
        public Result<DeleteOutcome> HardDeleteVersion(string bucket, string key, string versionId, bool bypassGovernance) => throw new NotImplementedException();
        public Result<IReadOnlyList<Result<DeleteOutcome>>> DeleteBatch(string bucket, IReadOnlyList<BatchDeleteItem> items) => throw new NotImplementedException();
        public Result<CurrentPage> ListCurrent(string bucket, string? prefix, KeyBound? from, int limit) => new CurrentPage([], false);
        public Result<VersionsPage> ListAllVersions(string bucket, string? prefix, string? keyMarker, int limit) => new VersionsPage([], false);
        public Result<VersioningStatus> GetVersioning(string bucket) => VersioningStatus.Unversioned;
        public Result SetVersioning(string bucket, VersioningStatus status) => Result.Ok;
        public Result<PutTaggingOutcome> PutTagging(string bucket, string key, string? versionId, IReadOnlyDictionary<string, string> tags) => throw new NotImplementedException();
        public VersionKind? GetCurrentKind(string bucket, string key) => null;
        public VersionKind? GetVersionKind(string bucket, string key, string versionId) => null;
        public Result<ObjectLockConfig?> GetObjectLock(string bucket) => (ObjectLockConfig?)null;
        public Result SetObjectLock(string bucket, ObjectLockConfig cfg) => Result.Ok;
        public Result<LifecycleConfig?> GetLifecycle(string bucket) => (LifecycleConfig?)null;
        public Result SetLifecycle(string bucket, LifecycleConfig cfg) => Result.Ok;
        public Result RemoveLifecycle(string bucket) => Result.Ok;
        public Result<WebsiteConfig?> GetWebsite(string bucket) => (WebsiteConfig?)null;
        public Result SetWebsite(string bucket, WebsiteConfig cfg) => Result.Ok;
        public Result RemoveWebsite(string bucket) => Result.Ok;
        public Result<BucketAccess> GetAccess(string bucket) => BucketAccess.Private;
        public Result SetAccess(string bucket, BucketAccess access) => Result.Ok;
        public Result<CorsConfig?> GetCors(string bucket) => (CorsConfig?)null;
        public Result SetCors(string bucket, CorsConfig cfg) => Result.Ok;
        public Result RemoveCors(string bucket) => Result.Ok;
        public IEnumerable<Bucket> OpenBuckets() => [];
        public Result PutRetention(string bucket, string key, string versionId, Retention retention, bool bypassGovernance) => throw new NotImplementedException();
        public Result<Retention?> GetRetention(string bucket, string key, string versionId) => (Retention?)null;
        public Result PutLegalHold(string bucket, string key, string versionId, bool on) => throw new NotImplementedException();
        public Result<bool> GetLegalHold(string bucket, string key, string versionId) => false;
        public IEnumerable<string> AllReferencedBlobs() => [];
        public void Dispose() { }
    }

    [Theory]
    [InlineData("my-bucket.s3.local:9000", "my-bucket")]
    [InlineData("my-bucket.s3.local", "my-bucket")]
    [InlineData("photos.localhost:9000", "photos")]
    [InlineData("DOCS.localhost:9000", "docs")]
    [InlineData("static-site.s3.example.com:443", "static-site")]
    public void Extracts_Valid_Virtual_Host_Bucket(string host, string expectedBucket)
    {
        string[] baseDomains = ["s3.local", "localhost", "s3.example.com"];
        var registry = new MockRegistry([]);
        var resolver = new VirtualHostResolver(registry, new VirtualHostOptions(baseDomains));

        Assert.True(resolver.TryExtractBucket(host, out var bucket));
        Assert.Equal(expectedBucket, bucket);
    }

    [Theory]
    [InlineData("localhost:9000")]
    [InlineData("s3.local")]
    [InlineData("s3.example.com:443")]
    [InlineData("127.0.0.1:9000")]
    [InlineData("192.168.1.100:9000")]
    [InlineData("[::1]:9000")]
    [InlineData("admin.localhost:9000")]
    [InlineData("admin.s3.local:9000")]
    [InlineData("-invalid.localhost:9000")]
    [InlineData("invalid-.localhost:9000")]
    [InlineData("ab.localhost:9000")]
    [InlineData("")]
    public void Rejects_Invalid_Or_Base_Host(string host)
    {
        string[] baseDomains = ["s3.local", "localhost", "s3.example.com"];
        var registry = new MockRegistry([]);
        var resolver = new VirtualHostResolver(registry, new VirtualHostOptions(baseDomains));

        Assert.False(resolver.TryExtractBucket(host, out _));
    }

    [Fact]
    public void Identifies_Admin_Hosts()
    {
        string[] baseDomains = ["s3.local", "localhost"];
        var registry = new MockRegistry([]);
        var resolver = new VirtualHostResolver(registry, new VirtualHostOptions(baseDomains));

        Assert.True(resolver.IsAdminHost("admin.localhost:9000"));
        Assert.True(resolver.IsAdminHost("admin.s3.local"));
        Assert.True(resolver.IsAdminHost("ADMIN.localhost"));

        Assert.False(resolver.IsAdminHost("localhost:9000"));
        Assert.False(resolver.IsAdminHost("other.localhost:9000"));
        Assert.False(resolver.IsAdminHost("admin.other.com"));
    }

    [Fact]
    public void Custom_Domain_Matches_Existing_Bucket()
    {
        string[] baseDomains = ["localhost"];
        var registry = new MockRegistry(["mysite.lan", "docs.company.internal"]);
        var resolver = new VirtualHostResolver(registry, new VirtualHostOptions(baseDomains));

        Assert.True(resolver.TryExtractBucket("mysite.lan:9000", out var b1));
        Assert.Equal("mysite.lan", b1);

        Assert.True(resolver.TryExtractBucket("docs.company.internal", out var b2));
        Assert.Equal("docs.company.internal", b2);

        Assert.False(resolver.TryExtractBucket("unknown.company.internal:9000", out _));
    }

    [Theory]
    [InlineData("example.com:80", "example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("[::1]:8080", "[::1]")]
    [InlineData("[::1]", "[::1]")]
    public void Strips_Port_Correctly(string host, string expected)
    {
        Assert.Equal(expected, VirtualHostResolver.StripPort(host));
    }
}
