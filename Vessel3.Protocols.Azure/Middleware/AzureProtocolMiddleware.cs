using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Azure.Auth;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Headers;
using Vessel3.Protocols.Azure.Serialization;

namespace Vessel3.Protocols.Azure.Middleware;

internal sealed class AzureProtocolMiddleware(
    IAzureVerifier verifier,
    IAzureActionDispatcher dispatcher,
    IAzureErrorXmlWriter errorXml) : IMiddleware
{

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (ctx.Request.Path.StartsWithSegments("/_admin") ||
            ctx.Request.Path.StartsWithSegments("/_ui") ||
            ctx.Request.Path.StartsWithSegments("/v1") ||
            ctx.Request.Path.StartsWithSegments("/v2") ||
            ctx.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        if (!AzureRequestParser.IsAzureRequest(ctx.Request))
        {
            await next(ctx);
            return;
        }

        var requestId = Guid.NewGuid().ToString("D");
        ctx.Items["AzureRequestId"] = requestId;

        var target = AzureRequestParser.Parse(ctx.Request);

        var authResult = verifier.Verify(ctx.Request, target.Account);
        if (!authResult.TryGetValue(out var caller, out var authErr))
        {
            AzureHeaderCodec.ApplyStandardResponseHeaders(ctx);
            var errResult = new AzureErrorResult(authErr, errorXml);
            await errResult.ExecuteAsync(ctx);
            return;
        }

        ctx.Items["CallerIdentity"] = caller;

        AzureHeaderCodec.ApplyStandardResponseHeaders(ctx);

        var result = await dispatcher.Dispatch(target, ctx);
        await result.ExecuteAsync(ctx);
    }
}
