using Vessel3.Server;

namespace Vessel3.Server.S3;

internal sealed class SigV4Middleware(ISigV4Verifier verifier, IHttpResultMapper http) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        var result = verifier.Verify(ctx.Request);
        if (result is Result<SignatureContext>.Failure f)
        {
            await http.Map(f.Error).ExecuteAsync(ctx);
            return;
        }

        ctx.Items["sigctx"] = ((Result<SignatureContext>.Success)result).Value;
        await next(ctx);
    }
}
