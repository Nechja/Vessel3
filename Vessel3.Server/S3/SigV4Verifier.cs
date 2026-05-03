using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Vessel3.Server;

namespace Vessel3.Server.S3;

internal sealed record SignatureContext(
    string Signature,
    byte[] SigningKey,
    string AmzDate,
    string Scope);

internal sealed record SigV4Options(string AccessKey, string Secret, string Region);

internal interface ISigV4Verifier
{
    Result<SignatureContext> Verify(HttpRequest req);
}

internal sealed class AlwaysPassVerifier : ISigV4Verifier
{
    private static readonly SignatureContext Empty = new(string.Empty, [], string.Empty, string.Empty);
    public Result<SignatureContext> Verify(HttpRequest req) => Empty;
}

internal sealed class SigV4Verifier(SigV4Options options) : ISigV4Verifier
{
    private const string AlgorithmPrefix = "AWS4-HMAC-SHA256 ";
    private const string Service = "s3";
    private const string Terminator = "aws4_request";
    private static readonly TimeSpan SkewAllowance = TimeSpan.FromMinutes(15);

    public Result<SignatureContext> Verify(HttpRequest req) =>
        req.Query.ContainsKey("X-Amz-Signature") ? VerifyPresigned(req) : VerifyHeader(req);

    private Result<SignatureContext> VerifyPresigned(HttpRequest req)
    {
        var algorithm = req.Query["X-Amz-Algorithm"].ToString();
        if (algorithm is not "AWS4-HMAC-SHA256")
            return new AuthorizationHeaderMalformedError($"Unsupported algorithm {algorithm}");

        var credential = req.Query["X-Amz-Credential"].ToString();
        var amzDate = req.Query["X-Amz-Date"].ToString();
        var expiresRaw = req.Query["X-Amz-Expires"].ToString();
        var signedHeaders = req.Query["X-Amz-SignedHeaders"].ToString();
        var signature = req.Query["X-Amz-Signature"].ToString();

        if (string.IsNullOrEmpty(credential) || string.IsNullOrEmpty(amzDate)
            || string.IsNullOrEmpty(expiresRaw) || string.IsNullOrEmpty(signedHeaders)
            || string.IsNullOrEmpty(signature))
            return new AuthorizationHeaderMalformedError("Missing presigned query params");

        var credParts = credential.Split('/');
        if (credParts.Length is not 5 || credParts[4] is not Terminator)
            return new AuthorizationHeaderMalformedError("Bad Credential format");

        var ak = credParts[0];
        var date = credParts[1];
        var reg = credParts[2];
        var svc = credParts[3];

        if (!ConstantTimeEquals(ak, options.AccessKey))
            return new InvalidAccessKeyIdError(ak);
        if (svc is not Service)
            return new AuthorizationHeaderMalformedError($"Unexpected service {svc}");
        if (reg != options.Region)
            return new AuthorizationHeaderMalformedError($"Unexpected region {reg}");
        if (amzDate.Length < 8 || amzDate[..8] != date)
            return new AuthorizationHeaderMalformedError("Credential date mismatches X-Amz-Date");

        if (!DateTime.TryParseExact(amzDate, "yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var requestTime))
            return new AuthorizationHeaderMalformedError("Bad X-Amz-Date format");

        if (!int.TryParse(expiresRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresSec)
            || expiresSec <= 0)
            return new AuthorizationHeaderMalformedError("Bad X-Amz-Expires");

        if (DateTime.UtcNow > requestTime.AddSeconds(expiresSec))
            return new RequestTimeTooSkewedError();

        var signedHeaderList = signedHeaders.Split(';');
        var canonical = BuildCanonicalRequest(req, signedHeaderList, "UNSIGNED-PAYLOAD", excludeQueryKey: "X-Amz-Signature");
        var canonicalHash = Sha256Hex(Encoding.UTF8.GetBytes(canonical));

        var scope = $"{date}/{reg}/{Service}/{Terminator}";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{canonicalHash}";

        var signingKey = DeriveSigningKey(options.Secret, date, reg);
        var expected = HmacSha256Hex(signingKey, stringToSign);

        return ConstantTimeEquals(expected, signature)
            ? new SignatureContext(signature, signingKey, amzDate, scope)
            : (Result<SignatureContext>)new SignatureDoesNotMatchError();
    }

    private Result<SignatureContext> VerifyHeader(HttpRequest req)
    {
        var auth = req.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(auth))
            return new MissingSecurityHeaderError("Authorization");

        if (!auth.StartsWith(AlgorithmPrefix, StringComparison.Ordinal))
            return new AuthorizationHeaderMalformedError("Algorithm not AWS4-HMAC-SHA256");

        if (!TryParseAuth(auth[AlgorithmPrefix.Length..], out var credential, out var signedHeaders, out var signature))
            return new AuthorizationHeaderMalformedError("Missing Credential / SignedHeaders / Signature");

        var credParts = credential.Split('/');
        if (credParts.Length is not 5 || credParts[4] is not Terminator)
            return new AuthorizationHeaderMalformedError("Bad Credential format");

        var ak = credParts[0];
        var date = credParts[1];
        var reg = credParts[2];
        var svc = credParts[3];

        if (!ConstantTimeEquals(ak, options.AccessKey))
            return new InvalidAccessKeyIdError(ak);

        if (svc is not Service)
            return new AuthorizationHeaderMalformedError($"Unexpected service {svc}");

        if (reg != options.Region)
            return new AuthorizationHeaderMalformedError($"Unexpected region {reg}");

        var amzDate = req.Headers["x-amz-date"].ToString();
        if (string.IsNullOrEmpty(amzDate))
            return new MissingSecurityHeaderError("x-amz-date");

        if (amzDate.Length < 8 || amzDate[..8] != date)
            return new AuthorizationHeaderMalformedError("Credential date mismatches x-amz-date");

        if (!DateTime.TryParseExact(amzDate, "yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var requestTime))
            return new AuthorizationHeaderMalformedError("Bad x-amz-date format");

        if ((DateTime.UtcNow - requestTime).Duration() > SkewAllowance)
            return new RequestTimeTooSkewedError();

        var contentSha = req.Headers["x-amz-content-sha256"].ToString();
        if (string.IsNullOrEmpty(contentSha))
            return new MissingSecurityHeaderError("x-amz-content-sha256");

        var signedHeaderList = signedHeaders.Split(';');
        var canonical = BuildCanonicalRequest(req, signedHeaderList, contentSha, excludeQueryKey: null);
        var canonicalHash = Sha256Hex(Encoding.UTF8.GetBytes(canonical));

        var scope = $"{date}/{reg}/{Service}/{Terminator}";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{canonicalHash}";

        var signingKey = DeriveSigningKey(options.Secret, date, reg);
        var expected = HmacSha256Hex(signingKey, stringToSign);

        return ConstantTimeEquals(expected, signature)
            ? new SignatureContext(signature, signingKey, amzDate, scope)
            : (Result<SignatureContext>)new SignatureDoesNotMatchError();
    }

    private bool TryParseAuth(string body, out string credential, out string signedHeaders, out string signature)
    {
        credential = string.Empty;
        signedHeaders = string.Empty;
        signature = string.Empty;
        foreach (var part in body.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("Credential=", StringComparison.Ordinal))
                credential = part["Credential=".Length..];
            else if (part.StartsWith("SignedHeaders=", StringComparison.Ordinal))
                signedHeaders = part["SignedHeaders=".Length..];
            else if (part.StartsWith("Signature=", StringComparison.Ordinal))
                signature = part["Signature=".Length..];
        }
        return credential.Length > 0 && signedHeaders.Length > 0 && signature.Length > 0;
    }

    private string BuildCanonicalRequest(HttpRequest req, string[] signedHeaders, string contentHash, string? excludeQueryKey)
    {
        var sb = new StringBuilder();
        sb.Append(req.Method).Append('\n');
        sb.Append(CanonicalUri(req.Path.Value ?? "/")).Append('\n');
        sb.Append(CanonicalQuery(req.Query, excludeQueryKey)).Append('\n');

        var sorted = signedHeaders.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        foreach (var h in sorted)
        {
            var v = req.Headers[h].ToString().Trim();
            sb.Append(h).Append(':').Append(v).Append('\n');
        }
        sb.Append('\n');
        sb.Append(string.Join(';', sorted)).Append('\n');
        sb.Append(contentHash);
        return sb.ToString();
    }

    private string CanonicalUri(string path)
    {
        if (path.Length == 0) return "/";
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++) segments[i] = PercentEncode(segments[i], encodeSlash: false);
        return string.Join('/', segments);
    }

    private string CanonicalQuery(IQueryCollection query, string? excludeKey)
    {
        if (query.Count is 0) return string.Empty;
        var pairs = new List<(string K, string V)>();
        foreach (var kv in query)
        {
            if (excludeKey is not null && string.Equals(kv.Key, excludeKey, StringComparison.Ordinal)) continue;
            var encodedKey = PercentEncode(kv.Key, encodeSlash: true);
            if (kv.Value.Count is 0)
            {
                pairs.Add((encodedKey, string.Empty));
                continue;
            }
            foreach (var v in kv.Value)
                pairs.Add((encodedKey, PercentEncode(v ?? string.Empty, encodeSlash: true)));
        }
        pairs.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.K, b.K);
            return c is not 0 ? c : string.CompareOrdinal(a.V, b.V);
        });
        var sb = new StringBuilder();
        for (var i = 0; i < pairs.Count; i++)
        {
            if (i > 0) sb.Append('&');
            sb.Append(pairs[i].K).Append('=').Append(pairs[i].V);
        }
        return sb.ToString();
    }

    private string PercentEncode(string s, bool encodeSlash)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if (IsUnreserved(b) || (!encodeSlash && b is (byte)'/'))
                sb.Append((char)b);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private bool IsUnreserved(byte b) =>
        b is (>= (byte)'A' and <= (byte)'Z')
            or (>= (byte)'a' and <= (byte)'z')
            or (>= (byte)'0' and <= (byte)'9')
            or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~';

    private byte[] DeriveSigningKey(string secretKey, string date, string reg)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        var kRegion = HmacSha256(kDate, reg);
        var kService = HmacSha256(kRegion, Service);
        return HmacSha256(kService, Terminator);
    }

    private byte[] HmacSha256(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private string HmacSha256Hex(byte[] key, string data) =>
        Convert.ToHexStringLower(HmacSha256(key, data));

    private string Sha256Hex(byte[] data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));

    private bool ConstantTimeEquals(string a, string b) =>
        a.Length == b.Length
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a),
            Encoding.ASCII.GetBytes(b));
}
