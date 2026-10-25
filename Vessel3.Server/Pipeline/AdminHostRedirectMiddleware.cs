using System.Collections.Frozen;

namespace Vessel3.Server.Pipeline;

internal sealed class AdminHostRedirectMiddleware(IVirtualHostResolver resolver) : IMiddleware
{
    private static readonly FrozenSet<string> BypassRoutes = new[]
    {
        WellKnownRoutes.Metrics,
        WellKnownRoutes.Healthz,
        WellKnownRoutes.Livez,
        WellKnownRoutes.Readyz,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (!resolver.IsAdminHost(ctx.Request.Host.Value))
        {
            await next(ctx);
            return;
        }

        if (BypassRoutes.Contains(ctx.Request.Path.Value ?? string.Empty))
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

        await next(ctx);
    }
}
