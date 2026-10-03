using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav.Auth;

internal sealed class WebDavBasicAuthenticator(
    string? rootAccessKey,
    string? rootSecretKey,
    IIdentityRegistry? identity) : IWebDavAuthenticator
{
    private readonly bool isUnauthenticated = rootAccessKey is null && rootSecretKey is null;

    public Result<CallerIdentity> Authenticate(HttpRequest request)
    {
        if (isUnauthenticated)
        {
            return CallerIdentity.System;
        }

        var authHeader = request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith(WebDavHeaders.BasicScheme, StringComparison.OrdinalIgnoreCase))
        {
            return new HttpError("Unauthorized", "Missing or invalid Basic authentication header", StatusCodes.Status401Unauthorized);
        }

        var b64 = authHeader[WebDavHeaders.BasicScheme.Length..].Trim();
        return !TryDecodeBasic(b64, out var key, out var secret)
            ? new HttpError("Unauthorized", "Malformed Basic authentication credentials", StatusCodes.Status401Unauthorized)
            : TryAuthenticateRoot(key, secret, out var rootCaller)
                ? rootCaller
                : TryAuthenticateUser(key, secret, out var userCaller)
                    ? userCaller
                    : new HttpError("Unauthorized", "Invalid credentials", StatusCodes.Status401Unauthorized);
    }

    private bool TryAuthenticateRoot(string key, string secret, out Result<CallerIdentity> result)
    {
        if (rootAccessKey is null || rootSecretKey is null || !string.Equals(key, rootAccessKey, StringComparison.Ordinal))
        {
            result = default!;
            return false;
        }

        var match = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(rootSecretKey), Encoding.UTF8.GetBytes(secret));
        result = match
            ? CallerIdentity.System
            : new HttpError("Unauthorized", "Invalid access key or secret", StatusCodes.Status401Unauthorized);
        return true;
    }

    private bool TryAuthenticateUser(string key, string secret, out Result<CallerIdentity> result)
    {
        if (identity is null || !identity.GetAccessKey(key).TryGetValue(out var keyEntry, out _) || keyEntry is null)
        {
            result = default!;
            return false;
        }

        var keySecretBytes = Encoding.UTF8.GetBytes(keyEntry.SecretKey);
        var reqSecretBytes = Encoding.UTF8.GetBytes(secret);
        var match = CryptographicOperations.FixedTimeEquals(keySecretBytes, reqSecretBytes);

        result = match && identity.AuthenticateAccessKey(key).TryGetValue(out var caller, out _)
            ? caller
            : new HttpError("Unauthorized", "Invalid or revoked access key", StatusCodes.Status401Unauthorized);
        return true;
    }

    private static bool TryDecodeBasic(string basicStr, out string key, out string secret)
    {
        key = string.Empty;
        secret = string.Empty;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(basicStr));
            var idx = decoded.IndexOf(':');
            if (idx <= 0)
            {
                return false;
            }

            key = decoded[..idx];
            secret = decoded[(idx + 1)..];
            return true;
        }
        catch
        {
            return false;
        }
    }
}
