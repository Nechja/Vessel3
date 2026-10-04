namespace Vessel3.Server.Pipeline;

internal sealed class AdminHostRedirectMiddleware(IVirtualHostResolver resolver) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (resolver.IsAdminHost(ctx.Request.Host.Value))
        {
            if (ctx.Request.Path.Equals(WellKnownRoutes.Metrics, StringComparison.OrdinalIgnoreCase)
                || ctx.Request.Path.Equals(WellKnownRoutes.Healthz, StringComparison.OrdinalIgnoreCase)
                || ctx.Request.Path.Equals(WellKnownRoutes.Livez, StringComparison.OrdinalIgnoreCase)
                || ctx.Request.Path.Equals(WellKnownRoutes.Readyz, StringComparison.OrdinalIgnoreCase))
            {
                await next(ctx);
                return;
            }

            if (ctx.Request.Path == "/" || !ctx.Request.Path.StartsWithSegments("/_ui"))
            {
                var target = ctx.Request.Path == "/" ? "/_ui/" : $"/_ui{ctx.Request.Path}{ctx.Request.QueryString}";
                ctx.Response.Redirect(target, permanent: false);
                return;
            }
        }

        await next(ctx);
    }
}
