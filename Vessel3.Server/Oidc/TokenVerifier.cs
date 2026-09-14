using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace Vessel3.Server.Oidc;

internal sealed record VerifiedIdentity(string Subject, IReadOnlyList<string> Audiences);

internal interface ITokenVerifier
{
    Task<Result<VerifiedIdentity>> Verify(string token, CancellationToken ct);
}

internal sealed class TokenVerifier(OidcOptions options, ISigningKeys keys, TimeProvider clock) : ITokenVerifier
{
    private static readonly TimeSpan Leeway = TimeSpan.FromSeconds(60);

    public async Task<Result<VerifiedIdentity>> Verify(string token, CancellationToken ct)
    {
        var parts = token.Split('.');
        if (parts.Length is not 3) return new InvalidIdentityTokenError("token is not a three-segment JWS");

        if (!TryDecode(parts[0], out var headerBytes) || !TryDecode(parts[1], out var payloadBytes) || !TryDecode(parts[2], out var signature))
            return new InvalidIdentityTokenError("token segment is not valid base64url");

        string alg, kid;
        try
        {
            using var header = JsonDocument.Parse(headerBytes);
            alg = Str(header.RootElement, "alg") ?? "";
            kid = Str(header.RootElement, "kid") ?? "";
            var typ = Str(header.RootElement, "typ");
            if (typ is not (null or "JWT" or "at+jwt")) return new InvalidIdentityTokenError($"unsupported token type {typ}");
        }
        catch (JsonException)
        {
            return new InvalidIdentityTokenError("token header is not JSON");
        }

        if (!SigningKey.SupportedAlgs.Contains(alg, StringComparer.Ordinal)) return new InvalidIdentityTokenError($"unsupported alg {alg}");
        if (kid.Length is 0) return new InvalidIdentityTokenError("token header has no kid");

        var key = await keys.Find(kid, ct);
        if (key is null || !key.Supports(alg)) return new InvalidIdentityTokenError("no signing key matches the token");
        if (!key.Verifies(alg, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature))
            return new InvalidIdentityTokenError("signature does not verify");

        try
        {
            using var payload = JsonDocument.Parse(payloadBytes);
            return Claims(payload.RootElement);
        }
        catch (JsonException)
        {
            return new InvalidIdentityTokenError("token payload is not JSON");
        }
    }

    private Result<VerifiedIdentity> Claims(JsonElement claims)
    {
        var audiences = Strings(claims, "aud");
        var subject = Str(claims, "sub") ?? Str(claims, "client_id");
        var now = clock.GetUtcNow();
        return Str(claims, "iss")?.TrimEnd('/') != options.Issuer ? new InvalidIdentityTokenError("issuer mismatch")
            : !AudienceAccepted(audiences) ? new InvalidIdentityTokenError("audience mismatch")
            : Long(claims, "exp") is not { } exp ? new InvalidIdentityTokenError("token has no exp")
            : now >= DateTimeOffset.FromUnixTimeSeconds(exp) + Leeway ? new ExpiredIdentityTokenError()
            : Long(claims, "nbf") is { } nbf && now + Leeway < DateTimeOffset.FromUnixTimeSeconds(nbf)
                ? new InvalidIdentityTokenError("token is not yet valid")
            : string.IsNullOrEmpty(subject) ? new InvalidIdentityTokenError("token has no subject")
            : options.RequiredClaim is { } required && !Strings(claims, required.Name).Contains(required.Value, StringComparer.Ordinal)
                ? new AccessDeniedError($"token lacks required claim {required.Name}={required.Value}")
            : new VerifiedIdentity(subject, audiences);
    }

    private bool AudienceAccepted(IReadOnlyList<string> audiences) =>
        audiences.Contains(options.ClientId, StringComparer.Ordinal)
        || (options.Audience is not null && audiences.Contains(options.Audience, StringComparer.Ordinal));

    private static bool TryDecode(string segment, out byte[] bytes)
    {
        try
        {
            bytes = Base64Url.DecodeFromChars(segment);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static IReadOnlyList<string> Strings(JsonElement el, string name)
    {
        return !el.TryGetProperty(name, out var v) ? []
            : v.ValueKind switch
            {
                JsonValueKind.String => [v.GetString()!],
                JsonValueKind.Array => [.. v.EnumerateArray().Where(e => e.ValueKind is JsonValueKind.String).Select(e => e.GetString()!)],
                _ => [],
            };
    }
}
