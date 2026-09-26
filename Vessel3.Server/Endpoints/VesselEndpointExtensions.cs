using Vessel3.Server.Admin;
using Vessel3.Server.S3;

namespace Vessel3.Server.Endpoints;

internal static class VesselEndpointExtensions
{
    public static void MapVesselEndpoints(this WebApplication app)
    {
        app.MapGet("/", static async (HttpContext ctx, IS3XmlWriter xml, IBucketRegistry registry) =>
        {
            RequestTrace.SetAction("ListBuckets");
            ctx.Response.ContentType = "application/xml";
            await xml.WriteListBuckets(ctx.Response.Body, registry.List(), ctx.RequestAborted);
        });

        var admin = app.MapGroup("/_admin");
        admin.MapPut("/gc", static (HttpContext ctx, IAdminService adminService) => adminService.RunGc(ctx));
        admin.MapPut("/lifecycle", static (HttpContext ctx, IAdminService adminService) => adminService.RunLifecycle(ctx));
        admin.MapPut("/compact", static (HttpContext ctx, IAdminService adminService) => adminService.RunCompact(ctx));
        admin.MapGet("/buckets/{bucket}/access", static (string bucket, HttpContext ctx, IAdminService adminService) => adminService.GetBucketAccess(bucket, ctx));
        admin.MapPut("/buckets/{bucket}/access", static (string bucket, HttpContext ctx, IAdminService adminService) => adminService.SetBucketAccess(bucket, ctx));

        app.MapGet("/{bucket}", static async (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Get, bucket, ctx);
            await result.ExecuteAsync(ctx);
        });

        app.MapPut("/{bucket}", static async (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Put, bucket, ctx);
            await result.ExecuteAsync(ctx);
        });

        app.MapDelete("/{bucket}", static async (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Delete, bucket, ctx);
            await result.ExecuteAsync(ctx);
        });

        app.MapPost("/{bucket}", static async (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Post, bucket, ctx);
            await result.ExecuteAsync(ctx);
        });

        app.MapMethods("/{bucket}", ["HEAD"], static async (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Head, bucket, ctx);
            await result.ExecuteAsync(ctx);
        });

        app.MapGet("/{bucket}/{**key}", static async (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Get, bucket, key, ctx);
            await result.ExecuteAsync(ctx);
        });

        app.MapPut("/{bucket}/{**key}", static async (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Put, bucket, key, ctx);
            await result.ExecuteAsync(ctx);
        });

        app.MapDelete("/{bucket}/{**key}", static async (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Delete, bucket, key, ctx);
            await result.ExecuteAsync(ctx);
        });

        app.MapPost("/{bucket}/{**key}", static async (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Post, bucket, key, ctx);
            await result.ExecuteAsync(ctx);
        });

        app.MapMethods("/{bucket}/{**key}", ["HEAD"], static async (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Head, bucket, key, ctx);
            await result.ExecuteAsync(ctx);
        });
    }
}
