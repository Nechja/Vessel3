using System.Text;
using Vessel3.Server.Ui;
using Xunit;

namespace Vessel3.Tests;

public class UiEndpointsTests
{
    private static string Basic(string user, string pass) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

    [Fact]
    public void BasicAuthOk_CorrectCredentials_Accepts()
    {
        Assert.True(UiEndpoints.BasicAuthOk(Basic("akia", "secret"), "akia", "secret"));
    }

    [Fact]
    public void BasicAuthOk_WrongPassword_Rejects()
    {
        Assert.False(UiEndpoints.BasicAuthOk(Basic("akia", "wrong"), "akia", "secret"));
    }

    [Fact]
    public void BasicAuthOk_WrongUser_Rejects()
    {
        Assert.False(UiEndpoints.BasicAuthOk(Basic("nobody", "secret"), "akia", "secret"));
    }

    [Fact]
    public void BasicAuthOk_MissingHeader_Rejects()
    {
        Assert.False(UiEndpoints.BasicAuthOk("", "akia", "secret"));
    }

    [Fact]
    public void BasicAuthOk_OtherScheme_Rejects()
    {
        Assert.False(UiEndpoints.BasicAuthOk("Bearer abc", "akia", "secret"));
    }

    [Fact]
    public void BasicAuthOk_InvalidBase64_Rejects()
    {
        Assert.False(UiEndpoints.BasicAuthOk("Basic !!!notbase64!!!", "akia", "secret"));
    }

    [Fact]
    public void BasicAuthOk_PayloadWithoutColon_Rejects()
    {
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("nocolon"));
        Assert.False(UiEndpoints.BasicAuthOk(header, "akia", "secret"));
    }

    [Fact]
    public void BasicAuthOk_ColonInPassword_Allows()
    {
        Assert.True(UiEndpoints.BasicAuthOk(Basic("akia", "se:cr:et"), "akia", "se:cr:et"));
    }

    [Fact]
    public void BasicAuthOk_UnicodeCredentials_HandlesCorrectly()
    {
        Assert.True(UiEndpoints.BasicAuthOk(Basic("akia", "pässwörd"), "akia", "pässwörd"));
    }

    [Theory]
    [InlineData("_framework/dotnet.js")]
    [InlineData("_framework/missing.wasm")]
    [InlineData("_content/MudBlazor/MudBlazor.min.css")]
    [InlineData("app.css")]
    [InlineData("favicon.ico")]
    public void IsAssetPath_StaticAssets_Matches(string rel)
    {
        Assert.True(UiEndpoints.IsAssetPath(rel));
    }

    [Theory]
    [InlineData("uploads")]
    [InlineData("admin")]
    [InlineData("buckets/demo-assets")]
    [InlineData("buckets/my.bucket.with.dots")]
    public void IsAssetPath_SpaRoutes_ReturnsFalse(string rel)
    {
        Assert.False(UiEndpoints.IsAssetPath(rel));
    }
}
