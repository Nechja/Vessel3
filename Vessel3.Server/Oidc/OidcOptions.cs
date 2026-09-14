namespace Vessel3.Server.Oidc;

internal sealed record ClaimRequirement(string Name, string Value)
{
    public static Result<ClaimRequirement?> Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return (ClaimRequirement?)null;
        var eq = raw.IndexOf('=', StringComparison.Ordinal);
        return eq <= 0 || eq == raw.Length - 1
            ? new InvalidArgumentError("VESSEL3_OIDC_REQUIRE_CLAIM must be name=value")
            : new ClaimRequirement(raw[..eq], raw[(eq + 1)..]);
    }
}

internal sealed record OidcOptions(string Issuer, string ClientId, string? Audience, ClaimRequirement? RequiredClaim)
{
    public static Result<OidcOptions?> FromEnvironment()
    {
        var issuer = Environment.GetEnvironmentVariable("VESSEL3_OIDC_ISSUER");
        var clientId = Environment.GetEnvironmentVariable("VESSEL3_OIDC_CLIENT_ID");
        var audience = Environment.GetEnvironmentVariable("VESSEL3_OIDC_AUDIENCE");
        var claim = Environment.GetEnvironmentVariable("VESSEL3_OIDC_REQUIRE_CLAIM");
        return From(issuer, clientId, audience, claim);
    }

    public static Result<OidcOptions?> From(string? issuer, string? clientId, string? audience, string? claim) =>
        string.IsNullOrEmpty(issuer) && string.IsNullOrEmpty(clientId) ? (OidcOptions?)null
        : string.IsNullOrEmpty(issuer) ? new InvalidArgumentError("VESSEL3_OIDC_ISSUER is required when VESSEL3_OIDC_CLIENT_ID is set")
        : string.IsNullOrEmpty(clientId) ? new InvalidArgumentError("VESSEL3_OIDC_CLIENT_ID is required when VESSEL3_OIDC_ISSUER is set")
        : !Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            ? new InvalidArgumentError("VESSEL3_OIDC_ISSUER must be an absolute http(s) URL")
        : ClaimRequirement.Parse(claim).TryGetValue(out var required, out var err)
            ? new OidcOptions(issuer.TrimEnd('/'), clientId, string.IsNullOrEmpty(audience) ? null : audience, required)
            : err;

    public string DiscoveryUrl => Issuer + "/.well-known/openid-configuration";
}
