using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vessel3.Primitives;

namespace Vessel3.Protocols.Oci;

internal interface IContainerRepoTokenService
{
    string CreateToken(string userId, IReadOnlyList<string> scopes, TimeSpan ttl);
    bool ValidateToken(string token, out string? userId, out IReadOnlyList<string>? scopes);
}

internal sealed class ContainerRepoTokenService : IContainerRepoTokenService
{
    private readonly byte[] signingKey;
    private readonly TimeProvider clock;

    public ContainerRepoTokenService(string? seedSecret = null, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        if (!string.IsNullOrEmpty(seedSecret))
        {
            signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(seedSecret));
        }
        else
        {
            signingKey = new byte[32];
            RandomNumberGenerator.Fill(signingKey);
        }
    }

    public string CreateToken(string userId, IReadOnlyList<string> scopes, TimeSpan ttl)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var exp = now + (long)ttl.TotalSeconds;

        var headerJson = """{"alg":"HS256","typ":"JWT"}""";
        var headerB64 = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(headerJson));

        var scopesJson = string.Join(",", scopes.Select(s => $"\"{JsonEncodedText.Encode(s)}\""));
        var payloadJson = $$"""{"iss":"vessel3-registry","sub":"{{JsonEncodedText.Encode(userId)}}","nbf":{{now}},"exp":{{exp}},"scopes":[{{scopesJson}}]}""";
        var payloadB64 = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson));

        var signingInput = $"{headerB64}.{payloadB64}";
        using var hmac = new HMACSHA256(signingKey);
        var sigBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        var sigB64 = Base64Url.EncodeToString(sigBytes);

        return $"{signingInput}.{sigB64}";
    }

    public bool ValidateToken(string token, out string? userId, out IReadOnlyList<string>? scopes)
    {
        userId = null;
        scopes = null;

        var parts = token.Split('.');
        if (parts.Length != 3) return false;

        var headerB64 = parts[0];
        var payloadB64 = parts[1];
        var sigB64 = parts[2];

        var signingInput = $"{headerB64}.{payloadB64}";
        using var hmac = new HMACSHA256(signingKey);
        var expectedSig = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));

        Span<byte> actualSig = stackalloc byte[32];
        if (!Base64Url.TryDecodeFromChars(sigB64, actualSig, out var written) || written != 32)
            return false;

        if (!CryptographicOperations.FixedTimeEquals(expectedSig, actualSig))
            return false;

        try
        {
            var payloadBytes = Base64Url.DecodeFromChars(payloadB64);
            using var doc = JsonDocument.Parse(payloadBytes);
            var root = doc.RootElement;

            if (root.TryGetProperty("exp", out var expProp))
            {
                var exp = expProp.GetInt64();
                if (exp < clock.GetUtcNow().ToUnixTimeSeconds())
                    return false;
            }

            if (root.TryGetProperty("sub", out var subProp))
            {
                userId = subProp.GetString();
            }

            if (root.TryGetProperty("scopes", out var scopesProp) && scopesProp.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in scopesProp.EnumerateArray())
                {
                    if (el.GetString() is { } s) list.Add(s);
                }
                scopes = list;
            }
            else
            {
                scopes = [];
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
