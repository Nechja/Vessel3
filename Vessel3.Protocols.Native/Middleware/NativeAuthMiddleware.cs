using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native;

internal sealed class NativeAuthMiddleware(
    IIdentityRegistry registry,
    IBucketRegistry buckets,
    NativeAuthOptions options,
    ITokenAuthenticator? tokenAuthenticator = null) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (!ctx.Request.Path.StartsWithSegments("/v1"))
        {
            await next(ctx);
            return;
        }

        RequestTrace.SetContext(protocol: "native");

        var callerResult = await AuthenticateCaller(ctx);
        if (!callerResult.TryGetValue(out var caller, out var authErr))
        {
            await WriteError(ctx, authErr);
            return;
        }

        if (caller is not null)
        {
            ctx.SetCallerIdentity(caller);
            RequestTrace.SetContext(actor: caller.Username);
            await next(ctx);
            return;
        }

        if (IsAnonymousAllowed(ctx))
        {
            await next(ctx);
            return;
        }

        await WriteError(ctx, new HttpError("Unauthorized", "Authentication required", 401));
    }

    private async Task<Result<CallerIdentity?>> AuthenticateCaller(HttpContext ctx)
    {
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..].Trim();
            if (tokenAuthenticator is null)
                return new HttpError("Unauthorized", "Bearer token authentication is not configured", 401);

            var verified = await tokenAuthenticator.AuthenticateToken(token, ctx.RequestAborted);
            return !verified.TryGetValue(out var id, out var err)
                ? new HttpError("Unauthorized", err.Message, 401)
                : (CallerIdentity?)id;
        }

        string? key = null;
        string? secret = null;

        if (authHeader.StartsWith("Vessel ", StringComparison.OrdinalIgnoreCase))
        {
            var creds = authHeader["Vessel ".Length..].Trim();
            var colon = creds.IndexOf(':');
            if (colon > 0)
            {
                key = creds[..colon];
                secret = creds[(colon + 1)..];
            }
        }
        else if (ctx.Request.Headers.TryGetValue("X-Vessel-Key", out var k) && ctx.Request.Headers.TryGetValue("X-Vessel-Secret", out var s))
        {
            key = k.ToString();
            secret = s.ToString();
        }

        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
        {
            return options.IsUnauthenticated
                ? (CallerIdentity?)CallerIdentity.System
                : (CallerIdentity?)null;
        }

        if (options.RootAccessKey is not null && options.RootSecretKey is not null &&
            string.Equals(key, options.RootAccessKey, StringComparison.Ordinal))
        {
            var rootSecretBytes = Encoding.UTF8.GetBytes(options.RootSecretKey);
            var reqSecretBytes = Encoding.UTF8.GetBytes(secret);
            return CryptographicOperations.FixedTimeEquals(rootSecretBytes, reqSecretBytes)
                ? (CallerIdentity?)new CallerIdentity("usr_admin", "admin", UserRole.Admin, options.RootAccessKey)
                : new HttpError("InvalidCredentials", "Invalid access key or secret", 401);
        }

        if (registry.GetAccessKey(key).TryGetValue(out var keyEntry, out _) && keyEntry is not null)
        {
            var keySecretBytes = Encoding.UTF8.GetBytes(keyEntry.SecretKey);
            var reqSecretBytes = Encoding.UTF8.GetBytes(secret);
            if (!CryptographicOperations.FixedTimeEquals(keySecretBytes, reqSecretBytes))
                return new HttpError("InvalidCredentials", "Invalid access key or secret", 401);

            var authenticated = registry.AuthenticateAccessKey(key);
            return !authenticated.TryGetValue(out var caller, out var err)
                ? new HttpError("InvalidCredentials", err.Message, 401)
                : (CallerIdentity?)caller;
        }

        return new HttpError("InvalidCredentials", "Invalid access key or secret", 401);
    }

    private bool IsAnonymousAllowed(HttpContext ctx)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
            return false;

        var path = ctx.Request.Path.Value ?? string.Empty;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4)
            return false;

        if (!string.Equals(segments[0], "v1", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[1], "buckets", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[3], "objects", StringComparison.OrdinalIgnoreCase))
            return false;

        var bucket = segments[2];
        return buckets.GetAccess(bucket).TryGetValue(out var access, out _) && access.PublicRead;
    }

    private static async Task WriteError(HttpContext ctx, Error error)
    {
        ctx.Response.StatusCode = error.Status;
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
            ctx.Response.Body,
            new ErrorDto(error.Code, error.Message),
            NativeJsonContext.Default.ErrorDto,
            ctx.RequestAborted);
    }
}
