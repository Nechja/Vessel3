using System.Security.Cryptography;
using System.Text;
using Vessel3.Server.Configuration;
using Vessel3.Server.Telemetry;

namespace Vessel3.Server.Pipeline;

internal sealed class MetricsEndpointMiddleware(
    VesselConfig config,
    BucketStatsCache cache,
    IMetricsRenderer renderer) : IMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (ctx.Request.Path.Equals(WellKnownRoutes.Metrics, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAuthorized(ctx, config.MetricsToken, config.MetricsAllowAnonymous))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var sb = new StringBuilder(4096);
            renderer.Render(sb, cache.Get());
            ctx.Response.ContentType = renderer.ContentType;
            await ctx.Response.WriteAsync(sb.ToString(), ctx.RequestAborted);
            return;
        }

        await next(ctx);
    }

    private static bool IsAuthorized(HttpContext ctx, string? token, bool allowAnonymous)
    {
        if (allowAnonymous)
        {
            return true;
        }

        var remote = ctx.Connection.RemoteIpAddress;
        var fromLoopback = remote is not null && System.Net.IPAddress.IsLoopback(remote);
        if (fromLoopback && string.IsNullOrEmpty(token))
        {
            return true;
        }

        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var auth = ctx.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!auth.StartsWith(prefix, StringComparison.Ordinal))
        {
            return fromLoopback;
        }

        var presented = auth[prefix.Length..];
        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
