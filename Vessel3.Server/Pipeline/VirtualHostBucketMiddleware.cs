using Microsoft.AspNetCore.Http;
using Vessel3.Server.S3;

namespace Vessel3.Server.Pipeline;

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
