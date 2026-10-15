using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Protocols.Native.Adapters;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class ObjectEndpoints
{
    public static IEndpointRouteBuilder MapObjectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/buckets/{bucket}/objects", static (NativeObjectAdapter adapter, string bucket, HttpContext ctx) => adapter.ListObjects(bucket, ctx));
        endpoints.MapGet("/v1/buckets/{bucket}/objects/{**key}", static (NativeObjectAdapter adapter, string bucket, string key, HttpContext ctx) => adapter.GetObject(bucket, key, ctx));
        endpoints.MapMethods("/v1/buckets/{bucket}/objects/{**key}", ["HEAD"], static (NativeObjectAdapter adapter, string bucket, string key, HttpContext ctx) => adapter.HeadObject(bucket, key, ctx));
        endpoints.MapPut("/v1/buckets/{bucket}/objects/{**key}", static (NativeObjectAdapter adapter, string bucket, string key, HttpContext ctx) => adapter.PutObject(bucket, key, ctx));
        endpoints.MapDelete("/v1/buckets/{bucket}/objects/{**key}", static (NativeObjectAdapter adapter, string bucket, string key, HttpContext ctx) => adapter.DeleteObject(bucket, key, ctx));
        endpoints.MapPost("/v1/buckets/{bucket}/uploads/{**key}", static (NativeObjectAdapter adapter, string bucket, string key, HttpContext ctx) => adapter.PostUpload(bucket, key, ctx));
        endpoints.MapPut("/v1/buckets/{bucket}/uploads/{**key}", static (NativeObjectAdapter adapter, string bucket, string key, HttpContext ctx) => adapter.UploadPart(bucket, key, ctx));
        endpoints.MapDelete("/v1/buckets/{bucket}/uploads/{**key}", static (NativeObjectAdapter adapter, string bucket, string key, HttpContext ctx) => adapter.AbortUpload(bucket, key, ctx));
        return endpoints;
    }
}
