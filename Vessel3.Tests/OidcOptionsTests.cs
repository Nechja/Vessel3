using Xunit;

namespace Vessel3.Tests;

public class OidcOptionsTests
{
    [Fact]
    public void Parse_UnsetOptions_IsDisabled()
    {
        Assert.True(OidcOptions.From(null, null, null, null).TryGetValue(out var o, out _));
        Assert.Null(o);
    }

    [Fact]
    public void Parse_MissingClientId_Fails()
    {
        Assert.False(OidcOptions.From("https://id.test", null, null, null).TryGetValue(out _, out _));
    }

    [Fact]
    public void Parse_MissingIssuer_Fails()
    {
        Assert.False(OidcOptions.From(null, "vessel3", null, null).TryGetValue(out _, out _));
    }

    [Fact]
    public void Parse_RelativeIssuer_Fails()
    {
        Assert.False(OidcOptions.From("id.test", "vessel3", null, null).TryGetValue(out _, out _));
    }

    [Fact]
    public void Parse_TrailingSlash_TrimsAndDerivesDiscoveryUrl()
    {
        Assert.True(OidcOptions.From("https://id.test/", "vessel3", "", null).TryGetValue(out var o, out _));
        Assert.Equal("https://id.test", o!.Issuer);
        Assert.Equal("https://id.test/.well-known/openid-configuration", o.DiscoveryUrl);
        Assert.Null(o.Audience);
        Assert.Null(o.RequiredClaim);
    }

    [Fact]
    public void Parse_ClaimRequirement_ParsesNameAndValue()
    {
        Assert.True(OidcOptions.From("https://id.test", "vessel3", "shared", "groups=vessel3-admins").TryGetValue(out var o, out _));
        Assert.Equal(new ClaimRequirement("groups", "vessel3-admins"), o!.RequiredClaim);
        Assert.Equal("shared", o.Audience);
    }

    [Theory]
    [InlineData("groups")]
    [InlineData("=x")]
    [InlineData("groups=")]
    public void Parse_MalformedClaim_Fails(string raw)
    {
        Assert.False(OidcOptions.From("https://id.test", "vessel3", null, raw).TryGetValue(out _, out _));
    }
}
