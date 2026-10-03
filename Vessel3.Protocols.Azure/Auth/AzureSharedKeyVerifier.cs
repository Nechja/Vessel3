using System.Buffers;
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
        var auth = req.Headers.Authorization.ToString();

        // If unauthenticated mode and no Authorization header or dev store account, allow
        if (string.IsNullOrEmpty(auth))
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

        // Resolve secret key
        var (keyBytes, caller) = ResolveKey(accountToUse);
        if (keyBytes is null || caller is null)
        {
            return new InvalidAccessKeyIdError(accountToUse);
        }

        // Build string to sign
        var stringToSign = BuildStringToSign(req, accountToUse);
        using var hmac = new HMACSHA256(keyBytes);
        var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        var expectedSig = Convert.ToBase64String(computedHash);

        var expectedBytes = Encoding.UTF8.GetBytes(expectedSig);
        var actualBytes = Encoding.UTF8.GetBytes(signature);

        if (expectedBytes.Length != actualBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            var detail = $"The MAC signature found in the HTTP request '{signature}' is not the same as any computed signature. Server used following string to sign: '{stringToSign}'.";
            return new AzureAuthenticationError(detail);
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

        if (identity is not null)
        {
            if (identity.GetAccessKey(account) is Result<AccessKey?>.Success { Value: { } ak } && !ak.IsRevoked)
            {
                if (identity.GetUser(ak.UserId) is Result<User?>.Success { Value: { } user } && user.Status is UserStatus.Active)
                {
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(ak.SecretKey); }
                    catch { bytes = Encoding.UTF8.GetBytes(ak.SecretKey); }
                    return (bytes, new CallerIdentity(user.Id, user.Username, user.Role, ak.Id));
                }
            }
        }

        return (null, null);
    }

    public static string BuildStringToSign(HttpRequest req, string account)
    {
        var method = req.Method;
        var headers = req.Headers;

        var contentEncoding = headers.ContentEncoding.ToString();
        var contentLanguage = headers.ContentLanguage.ToString();
        var contentLength = req.ContentLength is > 0 ? req.ContentLength.Value.ToString(CultureInfo.InvariantCulture) : "";
        var contentMd5 = headers["Content-MD5"].ToString();
        var contentType = headers.ContentType.ToString();

        var date = headers["x-ms-date"].Count > 0 ? "" : headers.Date.ToString();
        var ifModifiedSince = headers.IfModifiedSince.ToString();
        var ifMatch = headers.IfMatch.ToString();
        var ifNoneMatch = headers.IfNoneMatch.ToString();
        var ifUnmodifiedSince = headers.IfUnmodifiedSince.ToString();
        var range = headers.Range.ToString();

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
            var lower = k.ToLowerInvariant();
            if (lower.StartsWith("x-ms-", StringComparison.Ordinal))
            {
                var val = v.ToString().Trim();
                msHeaders[lower] = msHeaders.TryGetValue(lower, out var existing) ? $"{existing},{val}" : val;
            }
        }

        var sb = new StringBuilder();
        foreach (var (k, v) in msHeaders)
        {
            sb.Append(k).Append(':').Append(v).Append('\n');
        }
        return sb.ToString();
    }

    private static string BuildCanonicalizedResource(HttpRequest req, string account)
    {
        var sb = new StringBuilder();
        sb.Append('/').Append(account);

        var rawPath = req.Path.Value ?? "/";
        sb.Append(rawPath);

        if (req.Query.Count > 0)
        {
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
        }

        return sb.ToString();
    }
}
