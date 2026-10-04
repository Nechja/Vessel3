namespace Vessel3.Server.Pipeline;

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
