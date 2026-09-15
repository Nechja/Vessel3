using System.Text;
using Vessel3.Server.Ui;
using Xunit;

namespace Vessel3.Tests;

public class UiEndpointsTests
{
    private static string Basic(string user, string pass) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

    [Fact]
    public void BasicAuthOk_accepts_correct_credentials()
    {
        Assert.True(UiEndpoints.BasicAuthOk(Basic("akia", "secret"), "akia", "secret"));
    }

    [Fact]
    public void BasicAuthOk_rejects_wrong_password()
    {
        Assert.False(UiEndpoints.BasicAuthOk(Basic("akia", "wrong"), "akia", "secret"));
    }

    [Fact]
    public void BasicAuthOk_rejects_wrong_user()
    {
        Assert.False(UiEndpoints.BasicAuthOk(Basic("nobody", "secret"), "akia", "secret"));
    }

    [Fact]
    public void BasicAuthOk_rejects_missing_header()
    {
        Assert.False(UiEndpoints.BasicAuthOk("", "akia", "secret"));
    }

    [Fact]
    public void BasicAuthOk_rejects_other_scheme()
    {
        Assert.False(UiEndpoints.BasicAuthOk("Bearer abc", "akia", "secret"));
    }

    [Fact]
    public void BasicAuthOk_rejects_invalid_base64()
    {
        Assert.False(UiEndpoints.BasicAuthOk("Basic !!!notbase64!!!", "akia", "secret"));
    }

    [Fact]
    public void BasicAuthOk_rejects_payload_without_colon()
    {
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("nocolon"));
        Assert.False(UiEndpoints.BasicAuthOk(header, "akia", "secret"));
    }

    [Fact]
    public void BasicAuthOk_allows_colon_in_password()
    {
        Assert.True(UiEndpoints.BasicAuthOk(Basic("akia", "se:cr:et"), "akia", "se:cr:et"));
    }

    [Fact]
    public void BasicAuthOk_handles_unicode_credentials()
    {
        Assert.True(UiEndpoints.BasicAuthOk(Basic("akia", "pässwörd"), "akia", "pässwörd"));
    }

    [Theory]
    [InlineData("bucket/key.txt", "bucket", "key.txt")]
    [InlineData("bucket/a/b/c.bin", "bucket", "a/b/c.bin")]
    [InlineData("b/k", "b", "k")]
    public void TryParseUploadPath_splits_bucket_and_key(string path, string bucket, string key)
    {
        Assert.True(UiEndpoints.TryParseUploadPath(path, out var b, out var k));
        Assert.Equal(bucket, b);
        Assert.Equal(key, k);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bucket")]
    [InlineData("bucket/")]
    [InlineData("/key.txt")]
    public void TryParseUploadPath_rejects_incomplete_paths(string path)
    {
        Assert.False(UiEndpoints.TryParseUploadPath(path, out _, out _));
    }

    [Theory]
    [InlineData("_framework/dotnet.js")]
    [InlineData("_framework/missing.wasm")]
    [InlineData("_content/MudBlazor/MudBlazor.min.css")]
    [InlineData("app.css")]
    [InlineData("favicon.ico")]
    public void IsAssetPath_matches_static_assets(string rel)
    {
        Assert.True(UiEndpoints.IsAssetPath(rel));
    }

    [Theory]
    [InlineData("uploads")]
    [InlineData("admin")]
    [InlineData("buckets/demo-assets")]
    [InlineData("buckets/my.bucket.with.dots")]
    public void IsAssetPath_passes_spa_routes(string rel)
    {
        Assert.False(UiEndpoints.IsAssetPath(rel));
    }
}

public class UiSessionBearerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private static (Vessel3.Server.S3.CredentialStore Store, Vessel3.Server.S3.Credential Session) Issued()
    {
        var store = new Vessel3.Server.S3.CredentialStore(new Vessel3.Server.S3.Credential("AKIAROOT", "secret", null, null), new TestClock(T0));
        return (store, store.IssueSession("acct_kayla", TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Accepts_live_session()
    {
        var (store, session) = Issued();
        Assert.True(UiEndpoints.SessionBearerOk($"Bearer {session.AccessKey}:{session.SessionToken}", store, T0));
    }

    [Fact]
    public void Rejects_expired_session()
    {
        var (store, session) = Issued();
        Assert.False(UiEndpoints.SessionBearerOk($"Bearer {session.AccessKey}:{session.SessionToken}", store, T0 + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Rejects_wrong_token()
    {
        var (store, session) = Issued();
        Assert.False(UiEndpoints.SessionBearerOk($"Bearer {session.AccessKey}:nope", store, T0));
    }

    [Fact]
    public void Rejects_root_key_even_with_secret()
    {
        var (store, _) = Issued();
        Assert.False(UiEndpoints.SessionBearerOk("Bearer AKIAROOT:secret", store, T0));
    }

    [Fact]
    public void Rejects_unknown_key_and_malformed_headers()
    {
        var (store, session) = Issued();
        Assert.False(UiEndpoints.SessionBearerOk("Bearer ASIANOPE:x", store, T0));
        Assert.False(UiEndpoints.SessionBearerOk($"Bearer {session.AccessKey}", store, T0));
        Assert.False(UiEndpoints.SessionBearerOk($"Basic {session.AccessKey}:{session.SessionToken}", store, T0));
        Assert.False(UiEndpoints.SessionBearerOk("", store, T0));
    }

    [Fact]
    public void Private_paths_are_upload_and_admin()
    {
        Assert.True(UiEndpoints.IsPrivatePath("upload/b/k"));
        Assert.True(UiEndpoints.IsPrivatePath("admin/gc"));
        Assert.False(UiEndpoints.IsPrivatePath("config.json"));
        Assert.False(UiEndpoints.IsPrivatePath("index.html"));
        Assert.False(UiEndpoints.IsPrivatePath("_framework/blazor.webassembly.js"));
    }
}
