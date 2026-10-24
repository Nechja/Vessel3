using Vessel3.Server.Configuration;

namespace Vessel3.Server.Pipeline;

internal sealed class ProbeEndpointMiddleware(VesselConfig config) : IMiddleware
{
    private static readonly ReadOnlyMemory<byte> HealthyBytes = "{\"status\":\"healthy\"}"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> AliveBytes = "{\"status\":\"alive\"}"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> ReadyBytes = "{\"status\":\"ready\"}"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> NotReadyBytes = "{\"status\":\"not_ready\",\"error\":\"Storage data root is not accessible\"}"u8.ToArray();

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (ctx.Request.Path.Equals(WellKnownRoutes.Healthz, StringComparison.OrdinalIgnoreCase))
        {
            await WriteJson(ctx, StatusCodes.Status200OK, HealthyBytes);
            return;
        }

        if (ctx.Request.Path.Equals(WellKnownRoutes.Livez, StringComparison.OrdinalIgnoreCase))
        {
            await WriteJson(ctx, StatusCodes.Status200OK, AliveBytes);
            return;
        }

        if (ctx.Request.Path.Equals(WellKnownRoutes.Readyz, StringComparison.OrdinalIgnoreCase))
        {
            var isReady = Directory.Exists(config.DataRoot);
            var (statusCode, bytes) = isReady
                ? (StatusCodes.Status200OK, ReadyBytes)
                : (StatusCodes.Status503ServiceUnavailable, NotReadyBytes);

            await WriteJson(ctx, statusCode, bytes);
            return;
        }

        await next(ctx);
    }

    private static async Task WriteJson(HttpContext ctx, int statusCode, ReadOnlyMemory<byte> payload)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = VesselMediaTypes.Json;
        await ctx.Response.Body.WriteAsync(payload, ctx.RequestAborted);
    }
}
