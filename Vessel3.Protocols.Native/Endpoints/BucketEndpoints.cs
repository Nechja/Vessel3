using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Protocols.Native.Handlers;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class BucketEndpoints
{
    public static IEndpointRouteBuilder MapBucketEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/buckets", static (NativeBucketHandler handler, HttpContext ctx) => handler.ListBuckets(ctx));
        endpoints.MapPut("/v1/buckets/{bucket}", static (NativeBucketHandler handler, string bucket, HttpContext ctx) => handler.CreateBucket(bucket, ctx));
        endpoints.MapDelete("/v1/buckets/{bucket}", static (NativeBucketHandler handler, string bucket, HttpContext ctx) => handler.DeleteBucket(bucket, ctx));
        endpoints.MapGet("/v1/buckets/{bucket}/access", static (NativeBucketHandler handler, string bucket, HttpContext ctx) => handler.GetBucketAccess(bucket, ctx));
        endpoints.MapPut("/v1/buckets/{bucket}/access", static (NativeBucketHandler handler, string bucket, HttpContext ctx) => handler.PutBucketAccess(bucket, ctx));
        endpoints.MapGet("/v1/buckets/{bucket}/versioning", static (NativeBucketHandler handler, string bucket, HttpContext ctx) => handler.GetBucketVersioning(bucket, ctx));
        endpoints.MapPut("/v1/buckets/{bucket}/versioning", static (NativeBucketHandler handler, string bucket, HttpContext ctx) => handler.PutBucketVersioning(bucket, ctx));
        endpoints.MapGet("/v1/buckets/{bucket}/website", static (NativeBucketHandler handler, string bucket, HttpContext ctx) => handler.GetBucketWebsite(bucket, ctx));
        endpoints.MapPut("/v1/buckets/{bucket}/website", static (NativeBucketHandler handler, string bucket, HttpContext ctx) => handler.PutBucketWebsite(bucket, ctx));
        endpoints.MapDelete("/v1/buckets/{bucket}/website", static (NativeBucketHandler handler, string bucket, HttpContext ctx) => handler.DeleteBucketWebsite(bucket, ctx));

        return endpoints;
    }
}
