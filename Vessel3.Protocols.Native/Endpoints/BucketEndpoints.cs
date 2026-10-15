using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Protocols.Native.Adapters;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class BucketEndpoints
{
    public static IEndpointRouteBuilder MapBucketEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/buckets", static (NativeBucketAdapter adapter, HttpContext ctx) => adapter.ListBuckets(ctx));
        endpoints.MapPut("/v1/buckets/{bucket}", static (NativeBucketAdapter adapter, string bucket, HttpContext ctx) => adapter.CreateBucket(bucket, ctx));
        endpoints.MapDelete("/v1/buckets/{bucket}", static (NativeBucketAdapter adapter, string bucket, HttpContext ctx) => adapter.DeleteBucket(bucket, ctx));
        endpoints.MapGet("/v1/buckets/{bucket}/access", static (NativeBucketAdapter adapter, string bucket, HttpContext ctx) => adapter.GetBucketAccess(bucket, ctx));
        endpoints.MapPut("/v1/buckets/{bucket}/access", static (NativeBucketAdapter adapter, string bucket, HttpContext ctx) => adapter.PutBucketAccess(bucket, ctx));
        endpoints.MapGet("/v1/buckets/{bucket}/versioning", static (NativeBucketAdapter adapter, string bucket, HttpContext ctx) => adapter.GetBucketVersioning(bucket, ctx));
        endpoints.MapPut("/v1/buckets/{bucket}/versioning", static (NativeBucketAdapter adapter, string bucket, HttpContext ctx) => adapter.PutBucketVersioning(bucket, ctx));
        endpoints.MapGet("/v1/buckets/{bucket}/website", static (NativeBucketAdapter adapter, string bucket, HttpContext ctx) => adapter.GetBucketWebsite(bucket, ctx));
        endpoints.MapPut("/v1/buckets/{bucket}/website", static (NativeBucketAdapter adapter, string bucket, HttpContext ctx) => adapter.PutBucketWebsite(bucket, ctx));
        endpoints.MapDelete("/v1/buckets/{bucket}/website", static (NativeBucketAdapter adapter, string bucket, HttpContext ctx) => adapter.DeleteBucketWebsite(bucket, ctx));

        return endpoints;
    }
}
