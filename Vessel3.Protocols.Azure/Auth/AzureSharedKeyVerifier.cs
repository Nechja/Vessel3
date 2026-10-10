using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Auth;

internal interface IAzureVerifier
{
    Result<CallerIdentity> Verify(HttpRequest req, string? account);
}

internal sealed record AzureAuthenticationError(string Detail)
    : Error("AuthenticationFailed", "Server failed to authenticate the request. Make sure the value of Authorization header is formed correctly including the signature.")
{
    public override int Status => 403;
}

internal sealed class AzureSharedKeyVerifier : IAzureVerifier
{
    public const string DevStoreAccount = "devstoreaccount1";
    public const string DevStoreKeyBase64 = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly byte[] devStoreKeyBytes = Convert.FromBase64String(DevStoreKeyBase64);
    private readonly string? rootAccessKey;
    private readonly byte[]? rootKeyBytes;
    private readonly IIdentityRegistry? identity;
    private readonly bool isUnauthenticatedMode;

    public AzureSharedKeyVerifier(
        string? rootAccessKey,
        string? rootSecretKey,
        IIdentityRegistry? identity)
    {
        this.rootAccessKey = rootAccessKey;
        this.identity = identity;
        this.isUnauthenticatedMode = string.IsNullOrEmpty(rootAccessKey) && string.IsNullOrEmpty(rootSecretKey);

        if (!string.IsNullOrEmpty(rootSecretKey))
        {
            try
            {
                this.rootKeyBytes = Convert.FromBase64String(rootSecretKey);
            }
            catch (FormatException)
            {
                this.rootKeyBytes = Encoding.UTF8.GetBytes(rootSecretKey);
            }
        }
    }

    public Result<CallerIdentity> Verify(HttpRequest req, string? pathAccount)
    {
        if (!req.Headers.TryGetValue("Authorization", out var authVal) || authVal.Count == 0 || authVal[0] is not { Length: > 0 } auth)
        {
            return isUnauthenticatedMode
                ? CallerIdentity.System
                : new MissingSecurityHeaderError("Authorization");
        }

        const string sharedKeyPrefix = "SharedKey ";
        const string sharedKeyLitePrefix = "SharedKeyLite ";

        string headerAccount;
        string signature;

        if (auth.StartsWith(sharedKeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var token = auth[sharedKeyPrefix.Length..].Trim();
            var colon = token.IndexOf(':');
            if (colon <= 0) return new AuthorizationHeaderMalformedError("Malformed SharedKey header");
            headerAccount = token[..colon];
            signature = token[(colon + 1)..];
        }
        else if (auth.StartsWith(sharedKeyLitePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var token = auth[sharedKeyLitePrefix.Length..].Trim();
            var colon = token.IndexOf(':');
            if (colon <= 0) return new AuthorizationHeaderMalformedError("Malformed SharedKeyLite header");
            headerAccount = token[..colon];
            signature = token[(colon + 1)..];
        }
        else
        {
            return isUnauthenticatedMode
                ? CallerIdentity.System
                : new AuthorizationHeaderMalformedError($"Unsupported auth scheme in {auth}");
        }

        var accountToUse = !string.IsNullOrEmpty(headerAccount) ? headerAccount : (pathAccount ?? DevStoreAccount);

        var (keyBytes, caller) = ResolveKey(accountToUse);
        if (keyBytes is null || caller is null)
        {
            return new InvalidAccessKeyIdError(accountToUse);
        }

        var stringToSign = BuildStringToSign(req, accountToUse);
        Span<byte> hash = stackalloc byte[32];
        var utf8ByteCount = Encoding.UTF8.GetByteCount(stringToSign);
        byte[]? rented = null;
        Span<byte> utf8Bytes = utf8ByteCount <= 1024 ? stackalloc byte[1024] : (rented = ArrayPool<byte>.Shared.Rent(utf8ByteCount));
        try
        {
            var written = Encoding.UTF8.GetBytes(stringToSign, utf8Bytes);
            HMACSHA256.HashData(keyBytes, utf8Bytes[..written], hash);

            Span<byte> expectedSigBytes = stackalloc byte[64];
            Base64.EncodeToUtf8(hash, expectedSigBytes, out _, out var bytesWritten);

            Span<byte> actualBytes = stackalloc byte[64];
            var actualWritten = Encoding.UTF8.TryGetBytes(signature.AsSpan(), actualBytes, out var bytesEncoded) ? bytesEncoded : 0;

            if (actualWritten != bytesWritten ||
                !CryptographicOperations.FixedTimeEquals(expectedSigBytes[..bytesWritten], actualBytes[..actualWritten]))
            {
                var detail = $"The MAC signature found in the HTTP request '{signature}' is not the same as any computed signature. Server used following string to sign: '{stringToSign}'.";
                return new AzureAuthenticationError(detail);
            }
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }

        return caller;
    }

    private (byte[]? KeyBytes, CallerIdentity? Caller) ResolveKey(string account)
    {
        if (string.Equals(account, DevStoreAccount, StringComparison.OrdinalIgnoreCase))
        {
            return (devStoreKeyBytes, CallerIdentity.System);
        }

        if (rootAccessKey is not null && rootKeyBytes is not null)
        {
            if (string.Equals(account, rootAccessKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(account, "admin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(account, "vessel3", StringComparison.OrdinalIgnoreCase))
            {
                return (rootKeyBytes, CallerIdentity.System);
            }
        }

        if (identity is null)
        {
            return (null, null);
        }

        if (!identity.GetAccessKey(account).TryGetValue(out var accessKey, out _) || accessKey.IsRevoked)
        {
            return (null, null);
        }

        if (!identity.GetUser(accessKey.UserId).TryGetValue(out var user, out _) || user.Status is not UserStatus.Active)
        {
            return (null, null);
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(accessKey.SecretKey); }
        catch { bytes = Encoding.UTF8.GetBytes(accessKey.SecretKey); }
        return (bytes, new CallerIdentity(user.Id, user.Username, user.Role, accessKey.Id));
    }

    public static string BuildStringToSign(HttpRequest req, string account)
    {
        var method = req.Method;
        var headers = req.Headers;

        static string GetHeader(IHeaderDictionary hdrs, string key) =>
            hdrs.TryGetValue(key, out var val) && val.Count > 0 && val[0] is { Length: > 0 } s ? s : "";

        var contentEncoding = GetHeader(headers, "Content-Encoding");
        var contentLanguage = GetHeader(headers, "Content-Language");
        var contentLength = req.ContentLength is > 0 ? req.ContentLength.Value.ToString(CultureInfo.InvariantCulture) : "";
        var contentMd5 = GetHeader(headers, "Content-MD5");
        var contentType = GetHeader(headers, "Content-Type");

        var date = headers.ContainsKey("x-ms-date") ? "" : GetHeader(headers, "Date");
        var ifModifiedSince = GetHeader(headers, "If-Modified-Since");
        var ifMatch = GetHeader(headers, "If-Match");
        var ifNoneMatch = GetHeader(headers, "If-None-Match");
        var ifUnmodifiedSince = GetHeader(headers, "If-Unmodified-Since");
        var range = GetHeader(headers, "Range");

        var canonicalHeaders = BuildCanonicalizedHeaders(headers);
        var canonicalResource = BuildCanonicalizedResource(req, account);

        var sb = new StringBuilder(512);
        sb.Append(method).Append('\n');
        sb.Append(contentEncoding).Append('\n');
        sb.Append(contentLanguage).Append('\n');
        sb.Append(contentLength).Append('\n');
        sb.Append(contentMd5).Append('\n');
        sb.Append(contentType).Append('\n');
        sb.Append(date).Append('\n');
        sb.Append(ifModifiedSince).Append('\n');
        sb.Append(ifMatch).Append('\n');
        sb.Append(ifNoneMatch).Append('\n');
        sb.Append(ifUnmodifiedSince).Append('\n');
        sb.Append(range).Append('\n');
        sb.Append(canonicalHeaders);
        sb.Append(canonicalResource);

        return sb.ToString();
    }

    private static string BuildCanonicalizedHeaders(IHeaderDictionary headers)
    {
        var msHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in headers)
        {
            if (!k.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase)) continue;
            var lower = k.ToLowerInvariant();
            var val = v.ToString().Trim();
            msHeaders[lower] = msHeaders.TryGetValue(lower, out var existing) ? $"{existing},{val}" : val;
        }

        if (msHeaders.Count == 0) return string.Empty;

        var sb = new StringBuilder(msHeaders.Count * 64);
        foreach (var (k, v) in msHeaders)
        {
            sb.Append(k).Append(':').Append(v).Append('\n');
        }
        return sb.ToString();
    }

    private static string BuildCanonicalizedResource(HttpRequest req, string account)
    {
        var rawPath = req.Path.Value ?? "/";
        if (req.Query.Count == 0)
        {
            return $"/{account}{rawPath}";
        }

        var sb = new StringBuilder(64 + rawPath.Length);
        sb.Append('/').Append(account).Append(rawPath);

        var sortedParams = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (k, v) in req.Query)
        {
            if (string.IsNullOrEmpty(k)) continue;
            var lowerK = k.ToLowerInvariant();
            if (!sortedParams.TryGetValue(lowerK, out var list))
            {
                list = [];
                sortedParams[lowerK] = list;
            }
            foreach (var val in v)
            {
                if (val is not null) list.Add(Uri.UnescapeDataString(val));
            }
        }

        foreach (var (k, v) in sortedParams)
        {
            v.Sort(StringComparer.Ordinal);
            sb.Append('\n').Append(k).Append(':').Append(string.Join(',', v));
        }

        return sb.ToString();
    }
}
