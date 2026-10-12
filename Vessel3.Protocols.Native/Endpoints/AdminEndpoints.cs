using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Protocols.Native.Handlers;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/admin/gc", static (NativeAdminHandler handler, HttpContext ctx) => handler.RunGc(ctx));
        endpoints.MapPost("/v1/admin/sweep", static (NativeAdminHandler handler, HttpContext ctx) => handler.RunSweep(ctx));
        endpoints.MapGet("/v1/admin/logs", static (NativeAdminHandler handler, HttpContext ctx) => handler.GetLogs(ctx));
        endpoints.MapDelete("/v1/admin/logs", static (NativeAdminHandler handler, HttpContext ctx) => handler.ClearLogs(ctx));

        return endpoints;
    }
}
