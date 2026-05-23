using Vessel3.Server;
using Vessel3.Server.Storage;
using Xunit;

namespace Vessel3.Tests;

public class SuspendedVersioningTests : IDisposable
{
    private readonly string root;
    private readonly Bucket bucket;

    public SuspendedVersioningTests()
    {
        root = Path.Combine(Path.GetTempPath(), "vessel3-susp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bucket = new Bucket("susp", root);
        bucket.Open();
    }

    public void Dispose()
    {
        bucket.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static PutRequest Req(string body) => new(
        BlobSha: "deadbeef" + body.GetHashCode(StringComparison.Ordinal).ToString("x8", System.Globalization.CultureInfo.InvariantCulture),
        Md5: "00",
        Size: body.Length,
        ContentType: "text/plain",
        Metadata: new Dictionary<string, string>());

    [Fact]
    public void SuspendedPut_UsesNullVersionId()
    {
        bucket.SetVersioning(VersioningStatus.Suspended);
        var put = bucket.AppendPut("k", Req("first"));
        Assert.Equal("null", put.VersionId);
    }

    [Fact]
    public void SuspendedPut_RepeatedOverwritesNullVersion()
    {
        bucket.SetVersioning(VersioningStatus.Suspended);

        bucket.AppendPut("k", Req("first"));
        bucket.AppendPut("k", Req("second"));
        bucket.AppendPut("k", Req("third"));

        var (entries, _) = bucket.Index.ListAllVersions(prefix: null, keyMarker: null, limit: 100);
        var forKey = entries.Where(e => e.Key == "k").ToList();
        Assert.Single(forKey);
        Assert.Equal("null", forKey[0].VersionId);
    }

    [Fact]
    public void EnabledThenSuspendedThenEnabled_PreservesRealVersionIds()
    {
        bucket.SetVersioning(VersioningStatus.Enabled);
        var v1 = bucket.AppendPut("k", Req("v1")).VersionId;
        var v2 = bucket.AppendPut("k", Req("v2")).VersionId;
        Assert.NotEqual("null", v1);
        Assert.NotEqual("null", v2);
        Assert.NotEqual(v1, v2);

        bucket.SetVersioning(VersioningStatus.Suspended);
        var vNull = bucket.AppendPut("k", Req("vsusp")).VersionId;
        Assert.Equal("null", vNull);

        var (entries, _) = bucket.Index.ListAllVersions(prefix: null, keyMarker: null, limit: 100);
        var ids = entries.Where(e => e.Key == "k").Select(e => e.VersionId).ToHashSet();
        Assert.Contains(v1, ids);
        Assert.Contains(v2, ids);
        Assert.Contains("null", ids);
        Assert.Equal(3, ids.Count);

        bucket.SetVersioning(VersioningStatus.Enabled);
        var v3 = bucket.AppendPut("k", Req("v3")).VersionId;
        Assert.NotEqual("null", v3);

        var (entries2, _) = bucket.Index.ListAllVersions(prefix: null, keyMarker: null, limit: 100);
        var ids2 = entries2.Where(e => e.Key == "k").Select(e => e.VersionId).ToHashSet();
        Assert.Contains(v1, ids2);
        Assert.Contains(v2, ids2);
        Assert.Contains("null", ids2);
        Assert.Contains(v3, ids2);
        Assert.Equal(4, ids2.Count);
    }

    [Fact]
    public void SuspendedDelete_OverwritesNullVersion()
    {
        bucket.SetVersioning(VersioningStatus.Suspended);
        bucket.AppendPut("k", Req("first"));

        var outcome = ((Result<DeleteOutcome>.Success)bucket.AppendDelete("k", bypassGovernance: false)).Value;
        Assert.True(outcome.IsDeleteMarker);
        Assert.Equal("null", outcome.VersionId);

        var (entries, _) = bucket.Index.ListAllVersions(prefix: null, keyMarker: null, limit: 100);
        var forKey = entries.Where(e => e.Key == "k").ToList();
        Assert.Single(forKey);
        Assert.Equal("null", forKey[0].VersionId);
        Assert.IsType<AllVersionsEntry.Marker>(forKey[0]);
    }

    [Fact]
    public void SuspendedDelete_PreservesEnabledVersions()
    {
        bucket.SetVersioning(VersioningStatus.Enabled);
        var v1 = bucket.AppendPut("k", Req("v1")).VersionId;

        bucket.SetVersioning(VersioningStatus.Suspended);
        bucket.AppendDelete("k", bypassGovernance: false);

        var (entries, _) = bucket.Index.ListAllVersions(prefix: null, keyMarker: null, limit: 100);
        var ids = entries.Where(e => e.Key == "k").Select(e => e.VersionId).ToHashSet();
        Assert.Contains(v1, ids);
        Assert.Contains("null", ids);
        Assert.Equal(2, ids.Count);
    }
}
