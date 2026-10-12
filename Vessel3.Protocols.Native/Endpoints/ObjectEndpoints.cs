using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Protocols.Native.Handlers;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class ObjectEndpoints
{
    public static IEndpointRouteBuilder MapObjectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/buckets/{bucket}/objects", static (NativeObjectHandler handler, string bucket, HttpContext ctx) => handler.ListObjects(bucket, ctx));
        endpoints.MapGet("/v1/buckets/{bucket}/objects/{**key}", static (NativeObjectHandler handler, string bucket, string key, HttpContext ctx) => handler.GetObject(bucket, key, ctx));
        endpoints.MapMethods("/v1/buckets/{bucket}/objects/{**key}", ["HEAD"], static (NativeObjectHandler handler, string bucket, string key, HttpContext ctx) => handler.HeadObject(bucket, key, ctx));
        endpoints.MapPut("/v1/buckets/{bucket}/objects/{**key}", static (NativeObjectHandler handler, string bucket, string key, HttpContext ctx) => handler.PutObject(bucket, key, ctx));
        endpoints.MapDelete("/v1/buckets/{bucket}/objects/{**key}", static (NativeObjectHandler handler, string bucket, string key, HttpContext ctx) => handler.DeleteObject(bucket, key, ctx));
        endpoints.MapPost("/v1/buckets/{bucket}/uploads/{**key}", static (NativeObjectHandler handler, string bucket, string key, HttpContext ctx) => handler.PostUpload(bucket, key, ctx));
        endpoints.MapPut("/v1/buckets/{bucket}/uploads/{**key}", static (NativeObjectHandler handler, string bucket, string key, HttpContext ctx) => handler.UploadPart(bucket, key, ctx));
        endpoints.MapDelete("/v1/buckets/{bucket}/uploads/{**key}", static (NativeObjectHandler handler, string bucket, string key, HttpContext ctx) => handler.AbortUpload(bucket, key, ctx));
        return endpoints;
    }
}
