using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Server.S3;

namespace Vessel3.Server.Pipeline;

internal sealed class VirtualHostS3DispatchMiddleware(
    IS3BucketActionDispatcher bucketDispatcher,
    IS3KeyActionDispatcher keyDispatcher) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (ctx.Items.TryGetValue("VirtualHostBucket", out var vhObj) && vhObj is string vhBucket)
        {
            if (ReservedRoutePrefixes.Matches(ctx.Request.Path.Value)
                || ctx.Request.Path.Equals(WellKnownRoutes.Metrics, StringComparison.OrdinalIgnoreCase))
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
