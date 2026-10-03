using System.Buffers.Text;
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
    ContainerRepoAuthOptions options) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (!ctx.Request.Path.StartsWithSegments("/v2"))
        {
            await next(ctx);
            return;
        }

        // Token endpoint handles its own authentication to issue tokens
        if (ctx.Request.Path.Equals("/v2/token", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        var isPing = ctx.Request.Path.Equals("/v2", StringComparison.OrdinalIgnoreCase)
            || ctx.Request.Path.Equals("/v2/", StringComparison.OrdinalIgnoreCase);

        var authHeader = ctx.Request.Headers.Authorization.ToString().Trim();

        if (options.IsUnauthenticated && string.IsNullOrEmpty(authHeader) && !ctx.Request.Headers.ContainsKey("X-Vessel-Key"))
        {
            ctx.Items["CallerIdentity"] = CallerIdentity.System;
            await next(ctx);
            return;
        }

        if (ctx.Request.Headers.TryGetValue("X-Vessel-Key", out var vKey) &&
            ctx.Request.Headers.TryGetValue("X-Vessel-Secret", out var vSecret))
        {
            var authResult = AuthenticateBasic(vKey.ToString(), vSecret.ToString());
            if (!authResult.TryGetValue(out var vCaller, out var vErr))
            {
                await WriteOciError(ctx, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", vErr.Message);
                return;
            }

            ctx.Items["CallerIdentity"] = vCaller;
            if (!vCaller.CanWrite && IsWriteMethod(ctx.Request.Method))
            {
                await WriteOciError(ctx, StatusCodes.Status403Forbidden, "DENIED", "ReadOnly credentials cannot modify repositories");
                return;
            }

            await next(ctx);
            return;
        }

        if (string.IsNullOrEmpty(authHeader))
        {
            await Challenge(ctx, isPing);
            return;
        }

        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..].Trim();
            if (!tokenService.ValidateToken(token, out var userId, out var scopes))
            {
                await WriteOciError(ctx, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid or expired token");
                return;
            }

            var caller = userId is not null && identity.GetUser(userId).TryGetValue(out var user, out _) && user is not null
                ? new CallerIdentity(user.Id, user.Username, user.Role, "token")
                : CallerIdentity.System;

            ctx.Items["CallerIdentity"] = caller;
            ctx.Items["OciScopes"] = scopes ?? [];

            if (!IsOperationAuthorized(ctx, caller, scopes))
            {
                await WriteOciError(ctx, StatusCodes.Status403Forbidden, "DENIED", "Requested access to the resource is denied");
                return;
            }

            await next(ctx);
            return;
        }

        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var basicStr = authHeader["Basic ".Length..].Trim();
            if (!TryDecodeBasic(basicStr, out var key, out var secret))
            {
                await Challenge(ctx, isPing);
                return;
            }

            var authResult = AuthenticateBasic(key, secret);
            if (!authResult.TryGetValue(out var caller, out var err))
            {
                await WriteOciError(ctx, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", err.Message);
                return;
            }

            ctx.Items["CallerIdentity"] = caller;

            if (!caller.CanWrite && IsWriteMethod(ctx.Request.Method))
            {
                await WriteOciError(ctx, StatusCodes.Status403Forbidden, "DENIED", "ReadOnly credentials cannot modify repositories");
                return;
            }

            await next(ctx);
            return;
        }

        await Challenge(ctx, isPing);
    }

    private Result<CallerIdentity> AuthenticateBasic(string key, string secret)
    {
        if (options.RootAccessKey is not null && options.RootSecretKey is not null &&
            string.Equals(key, options.RootAccessKey, StringComparison.Ordinal))
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(options.RootSecretKey), Encoding.UTF8.GetBytes(secret))
                ? CallerIdentity.System
                : new OciUnauthorizedError("Invalid access key or secret");
        }

        if (identity.GetAccessKey(key).TryGetValue(out var keyEntry, out _) && keyEntry is not null)
        {
            var keySecretBytes = Encoding.UTF8.GetBytes(keyEntry.SecretKey);
            var reqSecretBytes = Encoding.UTF8.GetBytes(secret);
            return !CryptographicOperations.FixedTimeEquals(keySecretBytes, reqSecretBytes)
                ? new OciUnauthorizedError("Invalid access key or secret")
                : identity.AuthenticateAccessKey(key);
        }

        return new OciUnauthorizedError("Invalid credentials");
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

    private static bool IsOperationAuthorized(HttpContext ctx, CallerIdentity caller, IReadOnlyList<string>? scopes)
    {
        if (caller.IsAdmin) return true;
        if (!caller.CanWrite && IsWriteMethod(ctx.Request.Method)) return false;

        if (scopes is null || scopes.Count == 0) return true;

        var repo = ExtractRepoFromPath(ctx.Request.Path);
        if (string.IsNullOrEmpty(repo)) return true;

        var requiredAction = IsWriteMethod(ctx.Request.Method) ? "push" : "pull";
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

    private static Task Challenge(HttpContext ctx, bool isPing)
    {
        var scheme = ctx.Request.Scheme;
        var host = ctx.Request.Host.Value;
        var realm = $"{scheme}://{host}/v2/token";
        var service = host;

        var repo = ExtractRepoFromPath(ctx.Request.Path);
        var actions = IsWriteMethod(ctx.Request.Method) ? "pull,push" : "pull";
        var scope = string.IsNullOrEmpty(repo) ? "" : $",scope=\"repository:{repo}:{actions}\"";

        ctx.Response.Headers.Append("Www-Authenticate", $"Bearer realm=\"{realm}\",service=\"{service}\"{scope}");
        ctx.Response.Headers.Append("Docker-Distribution-API-Version", "registry/2.0");

        return WriteOciError(ctx, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "authentication required");
    }

    private static async Task WriteOciError(HttpContext ctx, int statusCode, string code, string message)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json";
        var errDto = new OciErrorResponseDto([new OciErrorItemDto(code, message, null)]);
        await JsonSerializer.SerializeAsync(ctx.Response.Body, errDto, OciJsonContext.Default.OciErrorResponseDto);
    }
}
