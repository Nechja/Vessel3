namespace Vessel3.Server.Pipeline;

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
