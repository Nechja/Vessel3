using System.Globalization;
using Vessel3.Server.Storage;

namespace Vessel3.Server.S3;

internal sealed class CorsAndAccessMiddleware(IBucketRegistry registry) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        var bucket = ResolveBucket(ctx);
        if (bucket is not null)
        {
            var cors = registry.GetCors(bucket).TryGetValue(out var c, out _) ? c : null;
            var access = registry.GetAccess(bucket).TryGetValue(out var a, out _) ? a : BucketAccess.Private;

            if (TryHandlePreflight(ctx, cors))
            {
                return;
            }

            ApplyCrossOriginHeaders(ctx, cors);
            EvaluateAnonymousAccess(ctx, access);
        }

        await next(ctx);
    }

    private string? ResolveBucket(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue("VirtualHostBucket", out var vhObj) && vhObj is string vhBucket)
        {
            return vhBucket;
        }

        if (ctx.Request.Path.StartsWithSegments("/_admin")
            || ctx.Request.Path.StartsWithSegments("/_ui")
            || ctx.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = ctx.Request.Path.Value ?? "/";
        if (path == "/") return null;

        var trimmed = path.TrimStart('/');
        var slashIdx = trimmed.IndexOf('/');
        var candidate = slashIdx >= 0 ? trimmed[..slashIdx] : trimmed;
        return registry.IsValidName(candidate) ? candidate : null;
    }

    private static bool TryHandlePreflight(HttpContext ctx, CorsConfig? cors)
    {
        if (!HttpMethods.IsOptions(ctx.Request.Method)) return false;

        var origin = ctx.Request.Headers.Origin.ToString();
        var reqMethod = ctx.Request.Headers["Access-Control-Request-Method"].ToString();
        if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(reqMethod)) return false;

        if (cors is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return true;
        }

        var reqHeaders = ctx.Request.Headers["Access-Control-Request-Headers"].ToString();
        var headersList = string.IsNullOrEmpty(reqHeaders)
            ? []
            : reqHeaders.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var matched = cors.FindPreflightMatch(origin, reqMethod, headersList);
        if (matched is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return true;
        }

        ctx.Response.Headers.AccessControlAllowOrigin = matched.AllowedOrigins.Contains("*") ? "*" : origin;
        ctx.Response.Headers.AccessControlAllowMethods = matched.AllowedMethods.Count > 0
            ? string.Join(", ", matched.AllowedMethods)
            : reqMethod;

        if (!string.IsNullOrEmpty(reqHeaders))
        {
            ctx.Response.Headers.AccessControlAllowHeaders = reqHeaders;
        }
        else if (matched.AllowedHeaders is { Count: > 0 })
        {
            ctx.Response.Headers.AccessControlAllowHeaders = string.Join(", ", matched.AllowedHeaders);
        }

        if (matched.MaxAgeSeconds.HasValue)
        {
            ctx.Response.Headers.AccessControlMaxAge = matched.MaxAgeSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (matched.ExposeHeaders is { Count: > 0 })
        {
            ctx.Response.Headers.AccessControlExposeHeaders = string.Join(", ", matched.ExposeHeaders);
        }

        ctx.Response.Headers.Vary = "Origin, Access-Control-Request-Headers, Access-Control-Request-Method";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        return true;
    }

    private static void ApplyCrossOriginHeaders(HttpContext ctx, CorsConfig? cors)
    {
        var actualOrigin = ctx.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(actualOrigin) || cors is null) return;

        var matched = cors.FindMatch(actualOrigin, ctx.Request.Method);
        if (matched is null) return;

        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers.AccessControlAllowOrigin = matched.AllowedOrigins.Contains("*") ? "*" : actualOrigin;
            ctx.Response.Headers.Append("Vary", "Origin");
            if (matched.ExposeHeaders is { Count: > 0 })
            {
                ctx.Response.Headers.AccessControlExposeHeaders = string.Join(", ", matched.ExposeHeaders);
            }
            return Task.CompletedTask;
        });
    }

    private static void EvaluateAnonymousAccess(HttpContext ctx, BucketAccess access)
    {
        if (!access.PublicRead || (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)))
            return;

        var sub = S3BucketSubresourceParser.From(ctx.Request.Query);
        if (sub is S3BucketSubresource.None or S3BucketSubresource.Location)
        {
            ctx.Items["AnonymousAllowed"] = true;
        }
    }
}
