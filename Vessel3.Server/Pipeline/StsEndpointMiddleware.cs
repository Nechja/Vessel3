using Microsoft.AspNetCore.Http;
using Vessel3.Server.Oidc;

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
