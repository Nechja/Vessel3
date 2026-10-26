using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Protocols.Native.Adapters;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/admin/gc", static (NativeAdminAdapter adapter, HttpContext ctx) => adapter.RunGc(ctx));
        endpoints.MapPost("/v1/admin/sweep", static (NativeAdminAdapter adapter, HttpContext ctx) => adapter.RunSweep(ctx));
        endpoints.MapGet("/v1/admin/logs", static (NativeAdminAdapter adapter, HttpContext ctx) => adapter.GetLogs(ctx));
        endpoints.MapDelete("/v1/admin/logs", static (NativeAdminAdapter adapter, HttpContext ctx) => adapter.ClearLogs(ctx));
        endpoints.MapGet("/v1/admin/cluster", static (NativeAdminAdapter adapter, HttpContext ctx) => adapter.GetClusterStatus(ctx));

        return endpoints;
    }
}
