using System.Security.Cryptography;
using System.Text;
using Vessel3.Server.Configuration;
using Vessel3.Server.Oidc;
using Vessel3.Server.S3;
using Vessel3.Server.Telemetry;

namespace Vessel3.Server.Pipeline;

internal sealed class MetricsEndpointMiddleware(
    VesselConfig config,
    BucketStatsCache cache,
    IMetricsRenderer renderer) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (ctx.Request.Path.Equals("/metrics", StringComparison.Ordinal))
        {
            if (!IsAuthorized(ctx, config.MetricsToken, config.MetricsAllowAnonymous))
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            var sb = new StringBuilder(4096);
            renderer.Render(sb, cache.Get());
            ctx.Response.ContentType = renderer.ContentType;
            await ctx.Response.WriteAsync(sb.ToString(), ctx.RequestAborted);
            return;
        }

        await next(ctx);
    }

    private static bool IsAuthorized(HttpContext ctx, string? token, bool allowAnonymous)
    {
        if (allowAnonymous) return true;

        var remote = ctx.Connection.RemoteIpAddress;
        var fromLoopback = remote is not null && System.Net.IPAddress.IsLoopback(remote);
        if (fromLoopback && string.IsNullOrEmpty(token)) return true;
        if (string.IsNullOrEmpty(token)) return false;

        var auth = ctx.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!auth.StartsWith(prefix, StringComparison.Ordinal)) return fromLoopback;

        var presented = auth[prefix.Length..];
        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

internal sealed class AdminHostRedirectMiddleware(IVirtualHostResolver resolver) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (resolver.IsAdminHost(ctx.Request.Host.Value))
        {
            if (ctx.Request.Path == "/" || !ctx.Request.Path.StartsWithSegments("/_ui"))
            {
                var target = ctx.Request.Path == "/" ? "/_ui/" : $"/_ui{ctx.Request.Path}{ctx.Request.QueryString}";
                ctx.Response.Redirect(target, permanent: false);
                return;
            }
        }

        await next(ctx);
    }
}

internal sealed class StsEndpointMiddleware(ISecurityTokenService stsService) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (stsService.Matches(ctx.Request))
        {
            await stsService.Handle(ctx);
            return;
        }

        await next(ctx);
    }
}

internal sealed class VirtualHostBucketMiddleware(IVirtualHostResolver resolver) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (resolver.TryExtractBucket(ctx.Request.Host.Value, out var vhBucket))
        {
            ctx.Items["VirtualHostBucket"] = vhBucket;
        }

        await next(ctx);
    }
}

internal sealed class WebsiteServingMiddleware(
    IBucketRegistry registry,
    IWebsiteService websiteService) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (ctx.Request.Path.StartsWithSegments("/_site", out var siteRemaining))
        {
            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
            {
                ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            var rel = siteRemaining.Value ?? string.Empty;
            var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var siteBucket = segments[0];
            if (segments.Length == 1 && !ctx.Request.Path.Value!.EndsWith('/'))
            {
                var qs = ctx.Request.QueryString.Value ?? string.Empty;
                ctx.Response.Redirect(ctx.Request.Path.Value + "/" + qs, permanent: false);
                return;
            }

            var subPath = segments.Length == 1 ? "/" : "/" + string.Join('/', segments.Skip(1));
            if (ctx.Request.Path.Value!.EndsWith('/') && !subPath.EndsWith('/'))
            {
                subPath += "/";
            }

            if (registry.GetWebsite(siteBucket) is Result<WebsiteConfig?>.Success { Value: not null })
            {
                RequestTrace.SetAction(HttpMethods.IsHead(ctx.Request.Method) ? "WebsiteHead" : "WebsiteGet");
                var res = await websiteService.Serve(siteBucket, subPath, ctx);
                await res.ExecuteAsync(ctx);
                return;
            }

            await Results.Text(
                "<!DOCTYPE html><html><head><title>404 Not Found</title></head><body><h1>404 Not Found</h1><p>Website hosting is not configured for this bucket.</p></body></html>",
                "text/html",
                statusCode: StatusCodes.Status404NotFound).ExecuteAsync(ctx);
            return;
        }

        if (ctx.Items.TryGetValue("VirtualHostBucket", out var vhObj) && vhObj is string vhBucket)
        {
            var hasAuth = ctx.Request.Headers.ContainsKey("Authorization")
                || ctx.Request.Query.ContainsKey("X-Amz-Signature");

            if (!hasAuth
                && (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
                && registry.GetWebsite(vhBucket) is Result<WebsiteConfig?>.Success { Value: not null })
            {
                RequestTrace.SetAction(HttpMethods.IsHead(ctx.Request.Method) ? "WebsiteHead" : "WebsiteGet");
                var res = await websiteService.Serve(
                    vhBucket,
                    ctx.Request.Path.Value ?? "/",
                    ctx);
                await res.ExecuteAsync(ctx);
                return;
            }
        }

        await next(ctx);
    }
}

internal sealed class VirtualHostS3DispatchMiddleware(
    IS3BucketActionDispatcher bucketDispatcher,
    IS3KeyActionDispatcher keyDispatcher) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (ctx.Items.TryGetValue("VirtualHostBucket", out var vhObj) && vhObj is string vhBucket)
        {
            if (ctx.Request.Path.StartsWithSegments("/_admin") ||
                ctx.Request.Path.StartsWithSegments("/_ui") ||
                ctx.Request.Path.StartsWithSegments("/_site") ||
                ctx.Request.Path.StartsWithSegments("/v1") ||
                ctx.Request.Path.StartsWithSegments("/v2") ||
                ctx.Request.Path.StartsWithSegments("/dav") ||
                ctx.Request.Path.StartsWithSegments("/webdav") ||
                ctx.Request.Path.Equals("/metrics", StringComparison.Ordinal))
            {
                await next(ctx);
                return;
            }

            var method = ctx.Request.Method;
            var path = ctx.Request.Path.Value ?? "/";

            if (path is "/" or "")
            {
                var actionResult = await bucketDispatcher.Dispatch(method, vhBucket, ctx);
                await actionResult.ExecuteAsync(ctx);
                return;
            }

            var key = path.TrimStart('/');
            var keyActionResult = await keyDispatcher.Dispatch(method, vhBucket, key, ctx);
            await keyActionResult.ExecuteAsync(ctx);
            return;
        }

        await next(ctx);
    }
}
