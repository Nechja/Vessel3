using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vessel3.Server.Oidc;

internal sealed class SigningKey
{
    private static readonly string[] EcAlgs = ["ES256", "ES384", "ES512"];
    private static readonly string[] RsaAlgs = ["RS256", "RS384", "RS512", "PS256", "PS384", "PS512"];
    public static readonly string[] SupportedAlgs = [.. EcAlgs, .. RsaAlgs];

    private readonly ECParameters? ec;
    private readonly string? curve;
    private readonly RSAParameters? rsa;
    private readonly string? declaredAlg;

    public string Kid { get; }

    private SigningKey(string kid, string? declaredAlg, ECParameters? ec, string? curve, RSAParameters? rsa)
    {
        Kid = kid;
        this.declaredAlg = declaredAlg;
        this.ec = ec;
        this.curve = curve;
        this.rsa = rsa;
    }

    public static SigningKey Ec(string kid, string crv, byte[] x, byte[] y, string? alg = null) =>
        new(kid, alg, new ECParameters { Curve = NamedCurve(crv), Q = new ECPoint { X = x, Y = y } }, crv, null);

    public static SigningKey Rsa(string kid, byte[] modulus, byte[] exponent, string? alg = null) =>
        new(kid, alg, null, null, new RSAParameters { Modulus = modulus, Exponent = exponent });

    public bool Supports(string alg) =>
        (declaredAlg is null || declaredAlg == alg)
        && (ec is not null ? curve == CurveFor(alg) : RsaAlgs.Contains(alg, StringComparer.Ordinal));

    public bool Verifies(string alg, ReadOnlySpan<byte> signingInput, ReadOnlySpan<byte> signature)
    {
        if (!Supports(alg)) return false;
        var hash = HashFor(alg);
        try
        {
            if (ec is { } ecParams)
            {
                using var ecdsa = ECDsa.Create(ecParams);
                return ecdsa.VerifyData(signingInput, signature, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            using var rsaKey = RSA.Create(rsa!.Value);
            var padding = alg[0] is 'P' ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
            return rsaKey.VerifyData(signingInput, signature, hash, padding);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static HashAlgorithmName HashFor(string alg) => alg[2..] switch
    {
        "384" => HashAlgorithmName.SHA384,
        "512" => HashAlgorithmName.SHA512,
        _ => HashAlgorithmName.SHA256,
    };

    private static string? CurveFor(string alg) => alg switch
    {
        "ES256" => "P-256",
        "ES384" => "P-384",
        "ES512" => "P-521",
        _ => null,
    };

    private static ECCurve NamedCurve(string crv) => crv switch
    {
        "P-384" => ECCurve.NamedCurves.nistP384,
        "P-521" => ECCurve.NamedCurves.nistP521,
        _ => ECCurve.NamedCurves.nistP256,
    };

    public static IReadOnlyList<SigningKey> ParseJwks(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        if (!doc.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind is not JsonValueKind.Array) return [];
        var list = new List<SigningKey>();
        foreach (var jwk in keys.EnumerateArray())
        {
            var kid = Str(jwk, "kid");
            var use = Str(jwk, "use");
            var alg = Str(jwk, "alg");
            if (kid is null || (use is not null && use is not "sig")) continue;
            if (alg is not null && !SupportedAlgs.Contains(alg, StringComparer.Ordinal)) continue;
            switch (Str(jwk, "kty"))
            {
                case "EC" when Str(jwk, "crv") is "P-256" or "P-384" or "P-521" && Bytes(jwk, "x") is { } x && Bytes(jwk, "y") is { } y:
                    list.Add(Ec(kid, Str(jwk, "crv")!, x, y, alg));
                    break;
                case "RSA" when Bytes(jwk, "n") is { } n && Bytes(jwk, "e") is { } e:
                    list.Add(Rsa(kid, n, e, alg));
                    break;
                default:
                    break;
            }
        }
        return list;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;

    private static byte[]? Bytes(JsonElement el, string name)
    {
        if (Str(el, name) is not { } s) return null;
        try { return Base64Url.DecodeFromChars(s); }
        catch (FormatException) { return null; }
    }
}

internal interface ISigningKeys
{
    Task<SigningKey?> Find(string kid, CancellationToken ct);
}

internal sealed class JwksSigningKeys(IOidcDiscovery discovery, HttpClient http, TimeProvider clock) : ISigningKeys, IDisposable
{
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private IReadOnlyDictionary<string, SigningKey> keys = new Dictionary<string, SigningKey>(StringComparer.Ordinal);
    private DateTimeOffset lastRefresh = DateTimeOffset.MinValue;

    public async Task<SigningKey?> Find(string kid, CancellationToken ct)
    {
        if (keys.TryGetValue(kid, out var key)) return key;
        await Refresh(ct);
        return keys.TryGetValue(kid, out key) ? key : null;
    }

    public void Dispose() => refreshLock.Dispose();

    private async Task Refresh(CancellationToken ct)
    {
        await refreshLock.WaitAsync(ct);
        try
        {
            if (clock.GetUtcNow() - lastRefresh < MinRefreshInterval) return;
            if ((await discovery.Get(ct))?.JwksUri is not { Length: > 0 } url) return;
            var fetched = SigningKey.ParseJwks(await http.GetByteArrayAsync(url, CancellationToken.None));
            keys = fetched.ToDictionary(k => k.Kid, StringComparer.Ordinal);
            lastRefresh = clock.GetUtcNow();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            lastRefresh = clock.GetUtcNow();
        }
        finally
        {
            refreshLock.Release();
        }
    }
}
