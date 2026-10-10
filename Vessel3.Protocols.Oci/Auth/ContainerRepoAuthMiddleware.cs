using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Storage;

namespace Vessel3.Protocols.Oci;

public sealed record ContainerRepoAuthOptions(bool IsUnauthenticated, string? RootAccessKey, string? RootSecretKey);

internal sealed class ContainerRepoAuthMiddleware(
    IIdentityRegistry identity,
    IContainerRepoTokenService tokenService,
    ContainerRepoAuthOptions options,
    ITokenAuthenticator? tokenAuthenticator = null) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/v2"))
        {
            await next(context);
            return;
        }

        RequestTrace.SetContext(protocol: "oci");

        if (context.Request.Path.Equals("/v2/token", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var isPing = context.Request.Path.Equals("/v2", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals("/v2/", StringComparison.OrdinalIgnoreCase);

        var authHeader = context.Request.Headers.Authorization.ToString().Trim();

        if (options.IsUnauthenticated && string.IsNullOrEmpty(authHeader) && !context.Request.Headers.ContainsKey("X-Vessel-Key"))
        {
            context.Items["CallerIdentity"] = CallerIdentity.System;
            await next(context);
            return;
        }

        if (context.Request.Headers.TryGetValue("X-Vessel-Key", out var vesselKey) &&
            context.Request.Headers.TryGetValue("X-Vessel-Secret", out var vesselSecret))
        {
            if (await HandleCustomHeadersAuth(context, vesselKey.ToString(), vesselSecret.ToString(), next))
            {
                return;
            }
        }

        if (string.IsNullOrEmpty(authHeader))
        {
            await Challenge(context, isPing);
            return;
        }

        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await HandleBearerAuth(context, authHeader, next);
            return;
        }

        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            await HandleBasicAuth(context, authHeader, isPing, next);
            return;
        }

        await Challenge(context, isPing);
    }

    private async Task<bool> HandleCustomHeadersAuth(HttpContext context, string vesselKey, string vesselSecret, RequestDelegate next)
    {
        var authResult = AuthenticateBasic(vesselKey, vesselSecret);
        if (!authResult.TryGetValue(out var caller, out var error))
        {
            await WriteOciError(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", error.Message);
            return true;
        }

        context.Items["CallerIdentity"] = caller;
        if (!caller.CanWrite && IsWriteMethod(context.Request.Method))
        {
            await WriteOciError(context, StatusCodes.Status403Forbidden, "DENIED", "ReadOnly credentials cannot modify repositories");
            return true;
        }

        await next(context);
        return true;
    }

    private async Task HandleBearerAuth(HttpContext context, string authHeader, RequestDelegate next)
    {
        var token = authHeader["Bearer ".Length..].Trim();
        if (tokenService.ValidateToken(token, out var userId, out var scopes))
        {
            var caller = userId is not null && identity.GetUser(userId).TryGetValue(out var user, out _) && user is not null
                ? new CallerIdentity(user.Id, user.Username, user.Role, "token")
                : CallerIdentity.System;

            context.Items["CallerIdentity"] = caller;
            context.Items["OciScopes"] = scopes ?? [];

            if (!IsOperationAuthorized(context, caller, scopes))
            {
                await WriteOciError(context, StatusCodes.Status403Forbidden, "DENIED", "Requested access to the resource is denied");
                return;
            }

            await next(context);
            return;
        }

        if (tokenAuthenticator is not null)
        {
            var verified = await tokenAuthenticator.AuthenticateToken(token, context.RequestAborted);
            if (verified.TryGetValue(out var oidcCaller, out _))
            {
                context.Items["CallerIdentity"] = oidcCaller;
                if (!oidcCaller.CanWrite && IsWriteMethod(context.Request.Method))
                {
                    await WriteOciError(context, StatusCodes.Status403Forbidden, "DENIED", "ReadOnly credentials cannot modify repositories");
                    return;
                }

                await next(context);
                return;
            }
        }

        await WriteOciError(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid or expired token");
    }

    private async Task HandleBasicAuth(HttpContext context, string authHeader, bool isPing, RequestDelegate next)
    {
        var basicStr = authHeader["Basic ".Length..].Trim();
        if (!TryDecodeBasic(basicStr, out var key, out var secret))
        {
            await Challenge(context, isPing);
            return;
        }

        var authResult = AuthenticateBasic(key, secret);
        if (!authResult.TryGetValue(out var caller, out var error))
        {
            await WriteOciError(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", error.Message);
            return;
        }

        context.Items["CallerIdentity"] = caller;

        if (!caller.CanWrite && IsWriteMethod(context.Request.Method))
        {
            await WriteOciError(context, StatusCodes.Status403Forbidden, "DENIED", "ReadOnly credentials cannot modify repositories");
            return;
        }

        await next(context);
    }

    private Result<CallerIdentity> AuthenticateBasic(string key, string secret)
    {
        if (options.RootAccessKey is not null && options.RootSecretKey is not null &&
            string.Equals(key, options.RootAccessKey, StringComparison.Ordinal))
        {
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(options.RootSecretKey), Encoding.UTF8.GetBytes(secret)))
                return new OciUnauthorizedError("Invalid access key or secret");

            return CallerIdentity.System;
        }

        if (!identity.GetAccessKey(key).TryGetValue(out var keyEntry, out _) || keyEntry is null)
            return new OciUnauthorizedError("Invalid credentials");

        var keySecretBytes = Encoding.UTF8.GetBytes(keyEntry.SecretKey);
        var reqSecretBytes = Encoding.UTF8.GetBytes(secret);
        if (!CryptographicOperations.FixedTimeEquals(keySecretBytes, reqSecretBytes))
            return new OciUnauthorizedError("Invalid access key or secret");

        return identity.AuthenticateAccessKey(key);
    }

    private static bool TryDecodeBasic(string basicStr, out string key, out string secret)
    {
        key = "";
        secret = "";
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(basicStr));
            var idx = decoded.IndexOf(':');
            if (idx <= 0) return false;
            key = decoded[..idx];
            secret = decoded[(idx + 1)..];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWriteMethod(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    private static bool IsOperationAuthorized(HttpContext context, CallerIdentity caller, IReadOnlyList<string>? scopes)
    {
        if (caller.IsAdmin) return true;
        if (!caller.CanWrite && IsWriteMethod(context.Request.Method)) return false;

        if (scopes is null || scopes.Count == 0) return true;

        var repo = ExtractRepoFromPath(context.Request.Path);
        if (string.IsNullOrEmpty(repo)) return true;

        var requiredAction = IsWriteMethod(context.Request.Method) ? "push" : "pull";
        var expectedScope = $"repository:{repo}:{requiredAction}";

        foreach (var scope in scopes)
        {
            if (scope.StartsWith($"repository:{repo}:", StringComparison.OrdinalIgnoreCase))
            {
                var actions = scope[(repo.Length + 12)..].Split(',');
                if (actions.Contains(requiredAction, StringComparer.OrdinalIgnoreCase) || actions.Contains("*", StringComparer.OrdinalIgnoreCase))
                    return true;
            }
            if (scope is "repository:*:pull,push" or "repository:*:*")
                return true;
        }

        return false;
    }

    private static string ExtractRepoFromPath(PathString path)
    {
        var val = path.Value ?? "";
        if (!val.StartsWith("/v2/", StringComparison.Ordinal)) return "";
        var sub = val[4..];
        var blobsIdx = sub.IndexOf("/blobs", StringComparison.OrdinalIgnoreCase);
        if (blobsIdx > 0) return sub[..blobsIdx];
        var manifestsIdx = sub.IndexOf("/manifests", StringComparison.OrdinalIgnoreCase);
        if (manifestsIdx > 0) return sub[..manifestsIdx];
        var tagsIdx = sub.IndexOf("/tags", StringComparison.OrdinalIgnoreCase);
        return tagsIdx > 0 ? sub[..tagsIdx] : "";
    }

    private static Task Challenge(HttpContext context, bool isPing)
    {
        var scheme = context.Request.Scheme;
        var host = context.Request.Host.Value;
        var realm = $"{scheme}://{host}/v2/token";
        var service = host;

        var repo = ExtractRepoFromPath(context.Request.Path);
        var actions = IsWriteMethod(context.Request.Method) ? "pull,push" : "pull";
        var scope = string.IsNullOrEmpty(repo) ? "" : $",scope=\"repository:{repo}:{actions}\"";

        context.Response.Headers.Append("Www-Authenticate", $"Bearer realm=\"{realm}\",service=\"{service}\"{scope}");
        context.Response.Headers.Append("Docker-Distribution-API-Version", "registry/2.0");

        return WriteOciError(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "authentication required");
    }

    private static async Task WriteOciError(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        var errorDto = new OciErrorResponseDto([new OciErrorItemDto(code, message, null)]);
        await JsonSerializer.SerializeAsync(context.Response.Body, errorDto, OciJsonContext.Default.OciErrorResponseDto);
    }
}
