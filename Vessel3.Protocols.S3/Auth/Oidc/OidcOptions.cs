namespace Vessel3.Server.Oidc;

internal sealed record ClaimRequirement(string Name, string Value)
{
    public static Result<ClaimRequirement?> Parse(string? raw, string varName = "VESSEL3_OIDC_REQUIRE_CLAIM")
    {
        if (string.IsNullOrEmpty(raw)) return (ClaimRequirement?)null;
        var eq = raw.IndexOf('=', StringComparison.Ordinal);
        return eq <= 0 || eq == raw.Length - 1
            ? new InvalidArgumentError($"{varName} must be name=value")
            : new ClaimRequirement(raw[..eq], raw[(eq + 1)..]);
    }
}

internal sealed record OidcOptions(
    string Issuer,
    string ClientId,
    string? Audience,
    ClaimRequirement? RequiredClaim,
    ClaimRequirement? AdminClaim = null,
    IReadOnlyList<string>? AdminUsers = null)
{
    public static Result<OidcOptions?> FromEnvironment()
    {
        var issuer = Environment.GetEnvironmentVariable("VESSEL3_OIDC_ISSUER");
        var clientId = Environment.GetEnvironmentVariable("VESSEL3_OIDC_CLIENT_ID");
        var audience = Environment.GetEnvironmentVariable("VESSEL3_OIDC_AUDIENCE");
        var claim = Environment.GetEnvironmentVariable("VESSEL3_OIDC_REQUIRE_CLAIM");
        var adminClaim = Environment.GetEnvironmentVariable("VESSEL3_OIDC_ADMIN_CLAIM");
        var adminUsersRaw = Environment.GetEnvironmentVariable("VESSEL3_ADMIN_USERS");
        var adminUsers = string.IsNullOrWhiteSpace(adminUsersRaw)
            ? null
            : (IReadOnlyList<string>)[.. adminUsersRaw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        return From(issuer, clientId, audience, claim, adminClaim, adminUsers);
    }

    public static Result<OidcOptions?> From(
        string? issuer,
        string? clientId,
        string? audience,
        string? claim,
        string? adminClaim = null,
        IReadOnlyList<string>? adminUsers = null) =>
        string.IsNullOrEmpty(issuer) && string.IsNullOrEmpty(clientId) ? (OidcOptions?)null
        : string.IsNullOrEmpty(issuer) ? new InvalidArgumentError("VESSEL3_OIDC_ISSUER is required when VESSEL3_OIDC_CLIENT_ID is set")
        : string.IsNullOrEmpty(clientId) ? new InvalidArgumentError("VESSEL3_OIDC_CLIENT_ID is required when VESSEL3_OIDC_ISSUER is set")
        : !Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            ? new InvalidArgumentError("VESSEL3_OIDC_ISSUER must be an absolute http(s) URL")
        : !ClaimRequirement.Parse(claim, "VESSEL3_OIDC_REQUIRE_CLAIM").TryGetValue(out var required, out var reqErr)
            ? reqErr
        : !ClaimRequirement.Parse(adminClaim, "VESSEL3_OIDC_ADMIN_CLAIM").TryGetValue(out var adminReq, out var admErr)
            ? admErr
        : new OidcOptions(
            issuer.TrimEnd('/'),
            clientId,
            string.IsNullOrEmpty(audience) ? null : audience,
            required,
            adminReq,
            adminUsers);

    public string DiscoveryUrl => Issuer + "/.well-known/openid-configuration";
}

