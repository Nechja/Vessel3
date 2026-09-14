using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Vessel3.Server.Oidc;

namespace Vessel3.Tests;

internal sealed class TestIssuer : IDisposable
{
    private readonly ECDsa ec256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa ec384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
    private readonly ECDsa ec521 = ECDsa.Create(ECCurve.NamedCurves.nistP521);
    private readonly RSA rsa = RSA.Create(2048);

    public string Issuer { get; }
    public string EcKid => "ec-256";
    public string Ec384Kid => "ec-384";
    public string Ec521Kid => "ec-521";
    public string RsaKid => "rsa-1";

    public TestIssuer(string issuer = "https://id.example.test") => Issuer = issuer;

    public SigningKey[] Keys() =>
    [
        EcKey(ec256, EcKid, "P-256"),
        EcKey(ec384, Ec384Kid, "P-384"),
        EcKey(ec521, Ec521Kid, "P-521"),
        RsaKey(),
    ];

    public SigningKey RsaKey(string? declaredAlg = null)
    {
        var p = rsa.ExportParameters(false);
        return SigningKey.Rsa(RsaKid, p.Modulus!, p.Exponent!, declaredAlg);
    }

    private static SigningKey EcKey(ECDsa key, string kid, string crv)
    {
        var p = key.ExportParameters(false);
        return SigningKey.Ec(kid, crv, p.Q.X!, p.Q.Y!);
    }

    public string Jwks()
    {
        var e256 = ec256.ExportParameters(false);
        var r = rsa.ExportParameters(false);
        return $$"""
            {"keys":[
              {"kty":"EC","kid":"{{EcKid}}","use":"sig","alg":"ES256","crv":"P-256","x":"{{B64(e256.Q.X!)}}","y":"{{B64(e256.Q.Y!)}}"},
              {"kty":"RSA","kid":"{{RsaKid}}","use":"sig","n":"{{B64(r.Modulus!)}}","e":"{{B64(r.Exponent!)}}"}
            ]}
            """;
    }

    public string Token(string payloadJson, string alg = "ES256", string? kid = null, string? typ = "JWT", bool corrupt = false)
    {
        var useKid = kid ?? DefaultKid(alg);
        var headerJson = typ is null
            ? $$"""{"alg":"{{alg}}","kid":"{{useKid}}"}"""
            : $$"""{"alg":"{{alg}}","typ":"{{typ}}","kid":"{{useKid}}"}""";
        var header = B64(Encoding.UTF8.GetBytes(headerJson));
        var payload = B64(Encoding.UTF8.GetBytes(payloadJson));
        var input = Encoding.ASCII.GetBytes(header + "." + payload);
        var sig = Sign(alg, input);
        if (corrupt && sig.Length > 0) sig[0] ^= 0xff;
        return header + "." + payload + "." + B64(sig);
    }

    private string DefaultKid(string alg) => alg switch
    {
        "ES384" => Ec384Kid,
        "ES512" => Ec521Kid,
        "RS256" or "RS384" or "RS512" or "PS256" or "PS384" or "PS512" => RsaKid,
        _ => EcKid,
    };

    private byte[] Sign(string alg, byte[] input)
    {
        var hash = alg.EndsWith("384", StringComparison.Ordinal) ? HashAlgorithmName.SHA384
            : alg.EndsWith("512", StringComparison.Ordinal) ? HashAlgorithmName.SHA512
            : HashAlgorithmName.SHA256;
        return alg switch
        {
            "none" => [],
            "ES256" => ec256.SignData(input, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
            "ES384" => ec384.SignData(input, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
            "ES512" => ec521.SignData(input, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
            _ => rsa.SignData(input, hash, alg[0] is 'P' ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1),
        };
    }

    public string Claims(DateTimeOffset now, string? aud = null, string? extra = null, string sub = "acct_kayla", int ttlSeconds = 300, string? iss = null) =>
        $$"""{"iss":"{{iss ?? Issuer}}","sub":"{{sub}}","aud":{{aud ?? "\"vessel3\""}},"iat":{{now.ToUnixTimeSeconds()}},"exp":{{now.ToUnixTimeSeconds() + ttlSeconds}}{{(extra is null ? "" : "," + extra)}}}""";

    public static string B64(byte[] bytes) => Base64Url.EncodeToString(bytes);

    public void Dispose()
    {
        ec256.Dispose();
        ec384.Dispose();
        ec521.Dispose();
        rsa.Dispose();
    }
}
