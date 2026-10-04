
using Vessel3.Primitives;

namespace Vessel3.Server.S3;

internal sealed class SigV4Middleware(ISigV4Verifier verifier, IHttpResultMapper http) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (ReservedRoutePrefixes.Matches(ctx.Request.Path.Value))
        {
            await next(ctx);
            return;
        }

        RequestTrace.SetContext(protocol: "s3");

        if (ctx.Items.ContainsKey("AnonymousAllowed"))
        {
            var hasAuth = ctx.Request.Headers.ContainsKey("Authorization")
                || ctx.Request.Query.ContainsKey("X-Amz-Signature");
            if (!hasAuth)
            {
                await next(ctx);
                return;
            }
        }

        if (!verifier.Verify(ctx.Request).TryGetValue(out var sigCtx, out var err))
        {
            await http.Map(err).ExecuteAsync(ctx);
            return;
        }

        ctx.Items["sigctx"] = sigCtx;
        if (sigCtx.Caller is not null)
        {
            ctx.Items["CallerIdentity"] = sigCtx.Caller;
            RequestTrace.SetContext(actor: sigCtx.Caller.Username);
        }
        await next(ctx);
    }
}
