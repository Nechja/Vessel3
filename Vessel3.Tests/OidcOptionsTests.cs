using Vessel3.Server.Oidc;
using Xunit;

namespace Vessel3.Tests;

public class OidcOptionsTests
{
    [Fact]
    public void Unset_means_disabled()
    {
        Assert.True(OidcOptions.From(null, null, null, null).TryGetValue(out var o, out _));
        Assert.Null(o);
    }

    [Fact]
    public void Issuer_without_client_id_fails()
    {
        Assert.False(OidcOptions.From("https://id.test", null, null, null).TryGetValue(out _, out _));
    }

    [Fact]
    public void Client_id_without_issuer_fails()
    {
        Assert.False(OidcOptions.From(null, "vessel3", null, null).TryGetValue(out _, out _));
    }

    [Fact]
    public void Relative_issuer_fails()
    {
        Assert.False(OidcOptions.From("id.test", "vessel3", null, null).TryGetValue(out _, out _));
    }

    [Fact]
    public void Trailing_slash_is_trimmed_and_discovery_url_derived()
    {
        Assert.True(OidcOptions.From("https://id.test/", "vessel3", "", null).TryGetValue(out var o, out _));
        Assert.Equal("https://id.test", o!.Issuer);
        Assert.Equal("https://id.test/.well-known/openid-configuration", o.DiscoveryUrl);
        Assert.Null(o.Audience);
        Assert.Null(o.RequiredClaim);
    }

    [Fact]
    public void Claim_requirement_parses_name_and_value()
    {
        Assert.True(OidcOptions.From("https://id.test", "vessel3", "shared", "groups=vessel3-admins").TryGetValue(out var o, out _));
        Assert.Equal(new ClaimRequirement("groups", "vessel3-admins"), o!.RequiredClaim);
        Assert.Equal("shared", o.Audience);
    }

    [Theory]
    [InlineData("groups")]
    [InlineData("=x")]
    [InlineData("groups=")]
    public void Malformed_claim_requirement_fails(string raw)
    {
        Assert.False(OidcOptions.From("https://id.test", "vessel3", null, raw).TryGetValue(out _, out _));
    }
}
