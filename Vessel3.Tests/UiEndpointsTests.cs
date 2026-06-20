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
