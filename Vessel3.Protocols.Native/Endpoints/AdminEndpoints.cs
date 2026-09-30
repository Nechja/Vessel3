using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Primitives;
using Vessel3.Protocols.Native;
using Vessel3.Protocols.Native.Serialization;
using Vessel3.Storage;
using Vessel3.Storage.Lifecycle;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/admin/gc", static async (HttpContext ctx, IGarbageCollector gc) =>
        {
            var caller = ctx.GetCaller();
            if (caller is null || !caller.IsAdmin)
            {
                return new AccessDeniedError("Admin role required").ToHttpResult();
            }

            var blobAge = long.TryParse(ctx.Request.Query["minBlobAgeSec"], out var b) ? b : 3600;
            var uploadAge = long.TryParse(ctx.Request.Query["minUploadAgeSec"], out var u) ? u : 604800;
            var report = await gc.Run(TimeSpan.FromSeconds(blobAge), TimeSpan.FromSeconds(uploadAge));
            return Results.Json(new GcReportDto(report.BlobsDeleted, report.UploadsReaped), NativeJsonContext.Default.GcReportDto);
        });

        endpoints.MapPost("/v1/admin/sweep", static (HttpContext ctx, ILifecycleSweeper sweeper) =>
        {
            var caller = ctx.GetCaller();
            if (caller is null || !caller.IsAdmin)
            {
                return new AccessDeniedError("Admin role required").ToHttpResult();
            }

            var now = DateTimeOffset.UtcNow;
            if (ctx.Request.Query.TryGetValue("now", out var nowRaw) &&
                DateTimeOffset.TryParse(nowRaw.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                now = parsed;
            }

            var report = sweeper.Run(now);
            return Results.Json(new SweepReportDto(report.Expired, report.MarkersReaped), NativeJsonContext.Default.SweepReportDto);
        });

        return endpoints;
    }
}
