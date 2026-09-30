using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Primitives;
using Vessel3.Protocols.Native;
using Vessel3.Protocols.Native.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class BucketEndpoints
{
    public static IEndpointRouteBuilder MapBucketEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/buckets", static (HttpContext ctx, IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            var buckets = caller is not null ? registry.List(caller) : registry.List();
            List<BucketDto> dtos = [.. buckets.Select(b => new BucketDto(b.Name, b.CreatedAt, b.OwnerId))];
            return Results.Json(dtos, NativeJsonContext.Default.ListBucketDto);
        });

        endpoints.MapPut("/v1/buckets/{bucket}", static (string bucket, HttpContext ctx, IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            var result = caller is not null ? registry.Create(bucket, caller) : registry.Create(bucket);
            return result.Match(_ => Results.Ok(), NativeHttpResult.ToHttpResult);
        });

        endpoints.MapDelete("/v1/buckets/{bucket}", static (string bucket, HttpContext ctx, IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            var result = caller is not null ? registry.Delete(bucket, caller) : registry.Delete(bucket);
            return result.Match(() => Results.NoContent(), NativeHttpResult.ToHttpResult);
        });

        endpoints.MapGet("/v1/buckets/{bucket}/access", static (string bucket, HttpContext ctx, IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure authFail)
            {
                return authFail.Error.ToHttpResult();
            }

            var result = registry.GetAccess(bucket);
            return result.Match(
                access => Results.Json(new BucketAccessDto(access.PublicRead, access.ReadOnly), NativeJsonContext.Default.BucketAccessDto),
                NativeHttpResult.ToHttpResult);
        });

        endpoints.MapPut("/v1/buckets/{bucket}/access", static async (string bucket, HttpContext ctx, IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure authFail)
            {
                return authFail.Error.ToHttpResult();
            }

            var body = await ctx.Request.ReadFromJsonAsync(NativeJsonContext.Default.BucketAccessDto);
            var result = registry.SetAccess(bucket, new BucketAccess(body.PublicRead, body.ReadOnly));
            return result.Match(() => Results.Ok(), NativeHttpResult.ToHttpResult);
        });

        endpoints.MapGet("/v1/buckets/{bucket}/versioning", static (string bucket, HttpContext ctx, IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
            {
                return authFail.Error.ToHttpResult();
            }

            var result = registry.GetVersioning(bucket);
            return result.Match(
                status => Results.Json(new BucketVersioningDto(status.ToString()), NativeJsonContext.Default.BucketVersioningDto),
                NativeHttpResult.ToHttpResult);
        });

        endpoints.MapPut("/v1/buckets/{bucket}/versioning", static async (string bucket, HttpContext ctx, IBucketRegistry registry) =>
        {
            var caller = ctx.GetCaller();
            if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure authFail)
            {
                return authFail.Error.ToHttpResult();
            }

            var body = await ctx.Request.ReadFromJsonAsync(NativeJsonContext.Default.BucketVersioningDto);
            if (!Enum.TryParse<VersioningStatus>(body.Status, ignoreCase: true, out var status))
            {
                return new InvalidArgumentError("Invalid versioning status").ToHttpResult();
            }

            var result = registry.SetVersioning(bucket, status);
            return result.Match(() => Results.Ok(), NativeHttpResult.ToHttpResult);
        });

        return endpoints;
    }
}
