using Vessel3.Server;

namespace Vessel3.Server.S3;

internal sealed class SigV4Middleware(ISigV4Verifier verifier, IHttpResultMapper http) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (!verifier.Verify(ctx.Request).TryGetValue(out var sigCtx, out var err))
        {
            await http.Map(err).ExecuteAsync(ctx);
            return;
        }

        ctx.Items["sigctx"] = sigCtx;
        await next(ctx);
    }
}
