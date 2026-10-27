using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Primitives;
using Vessel3.Protocols.Native.Adapters;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(VesselRoutes.GcRoute, static (NativeAdminAdapter adapter, HttpContext ctx) => adapter.RunGc(ctx));
        endpoints.MapPost(VesselRoutes.SweepRoute, static (NativeAdminAdapter adapter, HttpContext ctx) => adapter.RunSweep(ctx));
        endpoints.MapGet(VesselRoutes.LogsRoute, static (NativeAdminAdapter adapter, HttpContext ctx) => adapter.GetLogs(ctx));
        endpoints.MapDelete(VesselRoutes.LogsRoute, static (NativeAdminAdapter adapter, HttpContext ctx) => adapter.ClearLogs(ctx));
        endpoints.MapGet(VesselRoutes.ClusterStatusRoute, static (NativeAdminAdapter adapter, HttpContext ctx) => adapter.GetClusterStatus(ctx));

        return endpoints;
    }
}
