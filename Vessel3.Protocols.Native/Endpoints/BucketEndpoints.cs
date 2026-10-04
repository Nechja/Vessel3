using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Primitives;
using Vessel3.Protocols.Native.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class BucketEndpoints
{
    public static IEndpointRouteBuilder MapBucketEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/buckets", ListBuckets);
        endpoints.MapPut("/v1/buckets/{bucket}", CreateBucket);
        endpoints.MapDelete("/v1/buckets/{bucket}", DeleteBucket);
        endpoints.MapGet("/v1/buckets/{bucket}/access", GetBucketAccess);
        endpoints.MapPut("/v1/buckets/{bucket}/access", PutBucketAccess);
        endpoints.MapGet("/v1/buckets/{bucket}/versioning", GetBucketVersioning);
        endpoints.MapPut("/v1/buckets/{bucket}/versioning", PutBucketVersioning);
        endpoints.MapGet("/v1/buckets/{bucket}/website", GetBucketWebsite);
        endpoints.MapPut("/v1/buckets/{bucket}/website", PutBucketWebsite);
        endpoints.MapDelete("/v1/buckets/{bucket}/website", DeleteBucketWebsite);

        return endpoints;
    }

    private static IResult ListBuckets(HttpContext context, IBucketRegistry registry)
    {
        RequestTrace.SetTarget("ListBuckets");
        var caller = context.GetCaller();
        var buckets = caller is not null ? registry.List(caller) : registry.List();
        List<BucketDto> dtos = [.. buckets.Select(bucket => new BucketDto(bucket.Name, bucket.CreatedAt, bucket.OwnerId))];
        return Results.Json(dtos, NativeJsonContext.Default.ListBucketDto);
    }

    private static IResult CreateBucket(string bucket, HttpContext context, IBucketRegistry registry)
    {
        RequestTrace.SetTarget("CreateBucket", bucket);
        var caller = context.GetCaller();
        var result = caller is not null ? registry.Create(bucket, caller) : registry.Create(bucket);
        return result.Match(_ => Results.Ok(), NativeHttpResult.ToHttpResult);
    }

    private static IResult DeleteBucket(string bucket, HttpContext context, IBucketRegistry registry)
    {
        RequestTrace.SetTarget("DeleteBucket", bucket);
        var caller = context.GetCaller();
        var result = caller is not null ? registry.Delete(bucket, caller) : registry.Delete(bucket);
        return result.Match(() => Results.NoContent(), NativeHttpResult.ToHttpResult);
    }

    private static IResult GetBucketAccess(string bucket, HttpContext context, IBucketRegistry registry)
    {
        RequestTrace.SetTarget("GetBucketAccess", bucket);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var result = registry.GetAccess(bucket);
        return result.Match(
            access => Results.Json(new BucketAccessDto(access.PublicRead, access.ReadOnly), NativeJsonContext.Default.BucketAccessDto),
            NativeHttpResult.ToHttpResult);
    }

    private static async Task<IResult> PutBucketAccess(string bucket, HttpContext context, IBucketRegistry registry)
    {
        RequestTrace.SetTarget("PutBucketAccess", bucket);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var body = await context.Request.ReadFromJsonAsync(NativeJsonContext.Default.BucketAccessDto);
        var result = registry.SetAccess(bucket, new BucketAccess(body.PublicRead, body.ReadOnly));
        return result.Match(() => Results.Ok(), NativeHttpResult.ToHttpResult);
    }

    private static IResult GetBucketVersioning(string bucket, HttpContext context, IBucketRegistry registry)
    {
        RequestTrace.SetTarget("GetBucketVersioning", bucket);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var result = registry.GetVersioning(bucket);
        return result.Match(
            status => Results.Json(new BucketVersioningDto(status.ToString()), NativeJsonContext.Default.BucketVersioningDto),
            NativeHttpResult.ToHttpResult);
    }

    private static async Task<IResult> PutBucketVersioning(string bucket, HttpContext context, IBucketRegistry registry)
    {
        RequestTrace.SetTarget("PutBucketVersioning", bucket);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var body = await context.Request.ReadFromJsonAsync(NativeJsonContext.Default.BucketVersioningDto);
        if (!Enum.TryParse<VersioningStatus>(body.Status, ignoreCase: true, out var status))
        {
            return new InvalidArgumentError("Invalid versioning status").ToHttpResult();
        }

        var result = registry.SetVersioning(bucket, status);
        return result.Match(() => Results.Ok(), NativeHttpResult.ToHttpResult);
    }

    private static IResult GetBucketWebsite(string bucket, HttpContext context, IBucketRegistry registry)
    {
        RequestTrace.SetTarget("GetBucketWebsite", bucket);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var result = registry.GetWebsite(bucket);
        return result.Match(
            websiteConfig => websiteConfig is not null
                ? Results.Json(new BucketWebsiteDto(websiteConfig.IndexDocument, websiteConfig.ErrorDocument), NativeJsonContext.Default.BucketWebsiteDto)
                : Results.Json(new ErrorDto("NoSuchWebsiteConfiguration", $"The specified bucket does not have a website configuration: {bucket}"), NativeJsonContext.Default.ErrorDto, statusCode: StatusCodes.Status404NotFound),
            NativeHttpResult.ToHttpResult);
    }

    private static async Task<IResult> PutBucketWebsite(string bucket, HttpContext context, IBucketRegistry registry)
    {
        RequestTrace.SetTarget("PutBucketWebsite", bucket);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var body = await context.Request.ReadFromJsonAsync(NativeJsonContext.Default.BucketWebsiteDto);
        if (string.IsNullOrWhiteSpace(body.IndexDocument))
        {
            return new InvalidArgumentError("IndexDocument is required").ToHttpResult();
        }

        var result = registry.SetWebsite(bucket, new WebsiteConfig(body.IndexDocument, body.ErrorDocument));
        return result.Match(() => Results.Ok(), NativeHttpResult.ToHttpResult);
    }

    private static IResult DeleteBucketWebsite(string bucket, HttpContext context, IBucketRegistry registry)
    {
        RequestTrace.SetTarget("DeleteBucketWebsite", bucket);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var result = registry.RemoveWebsite(bucket);
        return result.Match(() => Results.NoContent(), NativeHttpResult.ToHttpResult);
    }
}
