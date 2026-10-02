using System.Text;
using Vessel3.Primitives;
using Vessel3.Protocols.Oci;
using Xunit;

namespace Vessel3.Tests;

public class ContainerRepoCatalogTests : IDisposable
{
    private readonly string root;
    private readonly TestClock clock = new(DateTimeOffset.UtcNow);
    private readonly List<IDisposable> disposables = [];

    public ContainerRepoCatalogTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-oci-catalog-{Guid.NewGuid():N}");
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

    private SqliteContainerRepoCatalog CreateCatalog()
    {
        var catalog = new SqliteContainerRepoCatalog(new ContainerRepoCatalogOptions(root), clock);
        disposables.Add(catalog);
        return catalog;
    }

    [Fact]
    public void GetOrCreateRepo_Normalizes_And_Is_Idempotent()
    {
        var catalog = CreateCatalog();

        var res1 = catalog.GetOrCreateRepo("/my-team/my-app/");
        Assert.True(res1.TryGetValue(out var repo1, out _));
        Assert.Equal("my-team/my-app", repo1.Name);

        var res2 = catalog.GetOrCreateRepo("MY-TEAM/MY-APP");
        Assert.True(res2.TryGetValue(out var repo2, out _));
        Assert.Equal("my-team/my-app", repo2.Name);

        var list = catalog.ListRepos();
        Assert.True(list.TryGetValue(out var repos, out _));
        Assert.Single(repos);
        Assert.Equal("my-team/my-app", repos[0]);
    }

    [Fact]
    public void PutManifest_And_GetManifest_ByTag_And_Digest()
    {
        var catalog = CreateCatalog();
        var payload = Encoding.UTF8.GetBytes("""{"schemaVersion":2,"mediaType":"application/vnd.docker.distribution.manifest.v2+json"}""");
        var layers = new[] { "sha256:1111111111111111111111111111111111111111111111111111111111111111" };

        var putRes = catalog.PutManifest("my-app", "v1.0.0", "application/vnd.docker.distribution.manifest.v2+json", payload, layers);
        Assert.True(putRes.TryGetValue(out var outcome, out _));
        Assert.StartsWith("sha256:", outcome.Digest, StringComparison.Ordinal);

        // Fetch by tag
        var tagRes = catalog.GetManifest("my-app", "v1.0.0");
        Assert.True(tagRes.TryGetValue(out var manifestByTag, out _));
        Assert.Equal(outcome.Digest, manifestByTag.Digest);
        Assert.Equal(payload, manifestByTag.Content);

        // Fetch by digest
        var digestRes = catalog.GetManifest("my-app", outcome.Digest);
        Assert.True(digestRes.TryGetValue(out var manifestByDigest, out _));
        Assert.Equal(outcome.Digest, manifestByDigest.Digest);

        // Check AllReferencedBlobs contains layers and manifest digest
        var referenced = catalog.AllReferencedBlobs().ToList();
        Assert.Contains("1111111111111111111111111111111111111111111111111111111111111111", referenced);
        Assert.Contains(outcome.Digest[7..], referenced);
    }

    [Fact]
    public void Tag_Update_And_Deletion()
    {
        var catalog = CreateCatalog();
        var payload1 = Encoding.UTF8.GetBytes("""{"ver":1}""");
        var payload2 = Encoding.UTF8.GetBytes("""{"ver":2}""");

        var put1 = catalog.PutManifest("web", "latest", "application/json", payload1, []);
        Assert.True(put1.TryGetValue(out var out1, out _));

        var get1 = catalog.GetManifest("web", "latest");
        Assert.True(get1.TryGetValue(out var m1, out _));
        Assert.Equal(out1.Digest, m1.Digest);

        // Move tag latest to payload2
        var put2 = catalog.PutManifest("web", "latest", "application/json", payload2, []);
        Assert.True(put2.TryGetValue(out var out2, out _));
        Assert.NotEqual(out1.Digest, out2.Digest);

        var get2 = catalog.GetManifest("web", "latest");
        Assert.True(get2.TryGetValue(out var m2, out _));
        Assert.Equal(out2.Digest, m2.Digest);

        // List tags
        var tagsRes = catalog.ListTags("web");
        Assert.True(tagsRes.TryGetValue(out var tags, out _));
        Assert.Single(tags);
        Assert.Equal("latest", tags[0]);

        // Delete tag
        var delRes = catalog.DeleteTag("web", "latest");
        Assert.True(delRes.TryGetValue(out var deleted, out _));
        Assert.True(deleted);

        var getAfterDel = catalog.GetManifest("web", "latest");
        Assert.True(getAfterDel.Match(_ => false, err => err is NoSuchManifestError));
    }

    [Fact]
    public void Pagination_For_Repos_And_Tags()
    {
        var catalog = CreateCatalog();
        for (var i = 1; i <= 5; i++)
        {
            catalog.GetOrCreateRepo($"repo-{i:D2}");
            catalog.PutManifest("repo-01", $"tag-{i:D2}", "application/json", Encoding.UTF8.GetBytes($"{i}"), []);
        }

        // List repos page 1 (limit 2)
        var p1 = catalog.ListRepos(limit: 2);
        Assert.True(p1.TryGetValue(out var r1, out _));
        Assert.Equal(2, r1.Count);
        Assert.Equal("repo-01", r1[0]);
        Assert.Equal("repo-02", r1[1]);

        // List repos page 2
        var p2 = catalog.ListRepos(limit: 2, last: r1[^1]);
        Assert.True(p2.TryGetValue(out var r2, out _));
        Assert.Equal(2, r2.Count);
        Assert.Equal("repo-03", r2[0]);
        Assert.Equal("repo-04", r2[1]);

        // List tags page 1
        var t1 = catalog.ListTags("repo-01", limit: 2);
        Assert.True(t1.TryGetValue(out var tags1, out _));
        Assert.Equal(2, tags1.Count);
        Assert.Equal("tag-01", tags1[0]);
        Assert.Equal("tag-02", tags1[1]);
    }

    [Fact]
    public void Upload_Session_Lifecycle_And_Expiry()
    {
        var catalog = CreateCatalog();

        var sessionRes = catalog.StartUploadSession("test-repo");
        Assert.True(sessionRes.TryGetValue(out var session, out _));
        Assert.True(File.Exists(session.TempFilePath));
        Assert.Equal(0, session.BytesReceived);

        catalog.UpdateUploadSession(session.Id, 1024);
        var getRes = catalog.GetUploadSession(session.Id);
        Assert.True(getRes.TryGetValue(out var updated, out _));
        Assert.Equal(1024, updated.BytesReceived);

        // Cancel
        var cancelRes = catalog.CancelUploadSession(session.Id);
        Assert.True(cancelRes.Match(() => true, _ => false));
        Assert.False(File.Exists(session.TempFilePath));

        var getAfterCancel = catalog.GetUploadSession(session.Id);
        Assert.True(getAfterCancel.Match(_ => false, err => err is BlobUploadUnknownError));
    }
}
