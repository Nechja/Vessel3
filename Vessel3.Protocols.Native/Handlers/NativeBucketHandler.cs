using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Native.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native.Handlers;

internal sealed class NativeBucketHandler(IBucketRegistry registry)
{
    public IResult ListBuckets(HttpContext context)
    {
        RequestTrace.SetTarget("ListBuckets");
        var caller = context.GetCaller();
        var buckets = caller is not null ? registry.List(caller) : registry.List();
        List<BucketDto> dtos = [.. buckets.Select(bucket => new BucketDto(bucket.Name, bucket.CreatedAt, bucket.OwnerId))];
        return Results.Json(dtos, NativeJsonContext.Default.ListBucketDto);
    }

    public IResult CreateBucket(string bucket, HttpContext context)
    {
        RequestTrace.SetTarget("CreateBucket", bucket);
        var caller = context.GetCaller();
        var result = caller is not null ? registry.Create(bucket, caller) : registry.Create(bucket);
        return result.TryGetValue(out _, out var err) ? Results.Ok() : err.ToHttpResult();
    }

    public IResult DeleteBucket(string bucket, HttpContext context)
    {
        RequestTrace.SetTarget("DeleteBucket", bucket);
        var caller = context.GetCaller();
        var result = caller is not null ? registry.Delete(bucket, caller) : registry.Delete(bucket);
        return result.TryGetError(out var err) ? err.ToHttpResult() : Results.NoContent();
    }

    public IResult GetBucketAccess(string bucket, HttpContext context)
    {
        RequestTrace.SetTarget("GetBucketAccess", bucket);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var result = registry.GetAccess(bucket);
        if (!result.TryGetValue(out var access, out var err))
        {
            return err.ToHttpResult();
        }

        return Results.Json(new BucketAccessDto(access.PublicRead, access.ReadOnly), NativeJsonContext.Default.BucketAccessDto);
    }

    public async Task<IResult> PutBucketAccess(string bucket, HttpContext context)
    {
        RequestTrace.SetTarget("PutBucketAccess", bucket);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var body = await context.Request.ReadFromJsonAsync(NativeJsonContext.Default.BucketAccessDto);
        var result = registry.SetAccess(bucket, new BucketAccess(body.PublicRead, body.ReadOnly));
        return result.TryGetError(out var err) ? err.ToHttpResult() : Results.Ok();
    }

    public IResult GetBucketVersioning(string bucket, HttpContext context)
    {
        RequestTrace.SetTarget("GetBucketVersioning", bucket);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var result = registry.GetVersioning(bucket);
        if (!result.TryGetValue(out var status, out var err))
        {
            return err.ToHttpResult();
        }

        return Results.Json(new BucketVersioningDto(status.ToString()), NativeJsonContext.Default.BucketVersioningDto);
    }

    public async Task<IResult> PutBucketVersioning(string bucket, HttpContext context)
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
        return result.TryGetError(out var err) ? err.ToHttpResult() : Results.Ok();
    }

    public IResult GetBucketWebsite(string bucket, HttpContext context)
    {
        RequestTrace.SetTarget("GetBucketWebsite", bucket);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var result = registry.GetWebsite(bucket);
        if (!result.TryGetValue(out var websiteConfig, out var err))
        {
            return err.ToHttpResult();
        }

        return websiteConfig is not null
            ? Results.Json(new BucketWebsiteDto(websiteConfig.IndexDocument, websiteConfig.ErrorDocument), NativeJsonContext.Default.BucketWebsiteDto)
            : Results.Json(new ErrorDto("NoSuchWebsiteConfiguration", $"The specified bucket does not have a website configuration: {bucket}"), NativeJsonContext.Default.ErrorDto, statusCode: StatusCodes.Status404NotFound);
    }

    public async Task<IResult> PutBucketWebsite(string bucket, HttpContext context)
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
        return result.TryGetError(out var err) ? err.ToHttpResult() : Results.Ok();
    }

    public IResult DeleteBucketWebsite(string bucket, HttpContext context)
    {
        RequestTrace.SetTarget("DeleteBucketWebsite", bucket);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var result = registry.RemoveWebsite(bucket);
        return result.TryGetError(out var err) ? err.ToHttpResult() : Results.NoContent();
    }
}
