using Vessel3.Server.Admin;
using Vessel3.Server.Hosting;

namespace Vessel3.Server.Endpoints;

internal static class VesselEndpointExtensions
{
    public static void MapVesselEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/_admin");
        admin.MapPut("/gc", static (HttpContext ctx, IAdminService adminService) => adminService.RunGc(ctx));
        admin.MapPut("/lifecycle", static (HttpContext ctx, IAdminService adminService) => adminService.RunLifecycle(ctx));
        admin.MapPut("/compact", static (HttpContext ctx, IAdminService adminService) => adminService.RunCompact(ctx));
        admin.MapGet("/buckets/{bucket}/access", static (string bucket, HttpContext ctx, IAdminService adminService) => adminService.GetBucketAccess(bucket, ctx));
        admin.MapPut("/buckets/{bucket}/access", static (string bucket, HttpContext ctx, IAdminService adminService) => adminService.SetBucketAccess(bucket, ctx));

        foreach (var protocol in app.Services.GetServices<IVesselProtocol>())
        {
            protocol.MapEndpoints(app);
        }
    }
}
