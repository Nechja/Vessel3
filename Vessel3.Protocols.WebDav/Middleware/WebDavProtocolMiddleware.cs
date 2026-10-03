using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.WebDav.Auth;
using Vessel3.Protocols.WebDav.Dispatch;
using Vessel3.Protocols.WebDav.Serialization;

namespace Vessel3.Protocols.WebDav.Middleware;

internal sealed class WebDavProtocolMiddleware(
    IWebDavAuthenticator authenticator,
    IWebDavActionDispatcher dispatcher) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (ctx.Request.Path.StartsWithSegments("/_admin") ||
            ctx.Request.Path.StartsWithSegments("/_ui") ||
            ctx.Request.Path.StartsWithSegments("/_site") ||
            ctx.Request.Path.StartsWithSegments("/v1") ||
            ctx.Request.Path.StartsWithSegments("/v2") ||
            ctx.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        if (!WebDavRequestParser.IsWebDavRequest(ctx.Request))
        {
            await next(ctx);
            return;
        }

        var target = WebDavRequestParser.Parse(ctx.Request);

        if (AllowsUnauthenticated(target.Operation))
        {
            var optionsResult = await dispatcher.Dispatch(target, ctx);
            await optionsResult.ExecuteAsync(ctx);
            return;
        }

        var authResult = authenticator.Authenticate(ctx.Request);
        if (!authResult.TryGetValue(out var caller, out var authErr))
        {
            var errResult = new WebDavErrorResult(authErr);
            await errResult.ExecuteAsync(ctx);
            return;
        }

        ctx.Items["CallerIdentity"] = caller;

        var result = await dispatcher.Dispatch(target, ctx);
        await result.ExecuteAsync(ctx);
    }

    private static bool AllowsUnauthenticated(WebDavOperationKind operation) =>
        operation is WebDavOperationKind.Options;
}
