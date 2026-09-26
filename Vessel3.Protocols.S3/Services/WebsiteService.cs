using System.Globalization;
using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3;

internal interface IWebsiteService
{
    Task<IResult> Serve(string bucket, string rawPath, HttpContext ctx);
}

internal sealed class WebsiteService(
    IBucketRegistry registry,
    IObjectStore objects,
    IPreconditionEvaluator preconditions) : IWebsiteService
{
    public Task<IResult> Serve(string bucket, string rawPath, HttpContext ctx)
    {
        if (registry.GetWebsite(bucket) is not Result<WebsiteConfig?>.Success { Value: { } cfg })
            return Task.FromResult<IResult>(Results.StatusCode(404));

        var path = (rawPath ?? "/").TrimStart('/');
        if (TryResolveDirectoryRedirect(bucket, path, cfg, ctx, out var redirectResult))
        {
            return Task.FromResult(redirectResult);
        }

        var targetKey = ResolveTargetKey(path, cfg);

        var result = HttpMethods.IsHead(ctx.Request.Method)
            ? ServeHead(bucket, targetKey, cfg, ctx)
            : ServeGet(bucket, targetKey, cfg, ctx);

        return Task.FromResult(result);
    }

    private bool TryResolveDirectoryRedirect(
        string bucket,
        string path,
        WebsiteConfig cfg,
        HttpContext ctx,
        out IResult redirect)
    {
        redirect = Results.Empty;
        if (string.IsNullOrEmpty(path) || path.EndsWith('/')) return false;

        if (objects.Stat(bucket, path, versionId: null) is Result<ObjectStat>.Success)
        {
            return false;
        }

        if (objects.Stat(bucket, $"{path}/{cfg.IndexDocument}", versionId: null) is Result<ObjectStat>.Success)
        {
            var query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : string.Empty;
            redirect = Results.Redirect($"/{path}/{query}", permanent: true);
            return true;
        }

        return false;
    }

    private static string ResolveTargetKey(string path, WebsiteConfig cfg) =>
        string.IsNullOrEmpty(path) ? cfg.IndexDocument
        : path.EndsWith('/') ? $"{path}{cfg.IndexDocument}"
        : path;

    private IResult ServeHead(
        string bucket,
        string targetKey,
        WebsiteConfig cfg,
        HttpContext ctx)
    {
        if (objects.Stat(bucket, targetKey, versionId: null) is Result<ObjectStat>.Success { Value: var stat })
        {
            var precond = preconditions.EvaluateForRead(ctx.Request.Headers, stat.Etag, stat.LastModified);
            if (precond is Precondition.NotModified) return Results.StatusCode(304);
            if (precond is Precondition.Failed) return Results.StatusCode(412);

            ctx.Response.ContentLength = stat.Size;
            ctx.Response.ContentType = stat.ContentType;
            ctx.Response.Headers.ETag = $"\"{stat.Etag}\"";
            ctx.Response.Headers.LastModified = stat.LastModified.ToString("R", CultureInfo.InvariantCulture);
            EmitSystemHeaders(ctx.Response.Headers, stat.SystemHeaders);
            return Results.Empty;
        }

        if (!string.IsNullOrEmpty(cfg.ErrorDocument)
            && objects.Stat(bucket, cfg.ErrorDocument, versionId: null) is Result<ObjectStat>.Success { Value: var errStat })
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentLength = errStat.Size;
            ctx.Response.ContentType = errStat.ContentType;
            return Results.Empty;
        }

        return Results.StatusCode(404);
    }

    private IResult ServeGet(
        string bucket,
        string targetKey,
        WebsiteConfig cfg,
        HttpContext ctx)
    {
        if (objects.Get(bucket, targetKey, versionId: null) is Result<StoredObject>.Success { Value: var obj })
        {
            var precond = preconditions.EvaluateForRead(ctx.Request.Headers, obj.Etag, obj.LastModified);
            if (precond is Precondition.NotModified)
            {
                obj.Body.Dispose();
                return Results.StatusCode(304);
            }
            if (precond is Precondition.Failed)
            {
                obj.Body.Dispose();
                return Results.StatusCode(412);
            }

            ctx.Response.Headers.ETag = $"\"{obj.Etag}\"";
            EmitSystemHeaders(ctx.Response.Headers, obj.SystemHeaders);

            return Results.File(
                obj.Body,
                obj.ContentType,
                lastModified: obj.LastModified,
                enableRangeProcessing: true);
        }

        return ServeErrorDocumentOrDefault(bucket, cfg, ctx);
    }

    private IResult ServeErrorDocumentOrDefault(
        string bucket,
        WebsiteConfig cfg,
        HttpContext ctx)
    {
        if (!string.IsNullOrEmpty(cfg.ErrorDocument)
            && objects.Get(bucket, cfg.ErrorDocument, versionId: null) is Result<StoredObject>.Success { Value: var errObj })
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Headers.ETag = $"\"{errObj.Etag}\"";
            EmitSystemHeaders(ctx.Response.Headers, errObj.SystemHeaders);

            return Results.File(
                errObj.Body,
                errObj.ContentType,
                lastModified: errObj.LastModified,
                enableRangeProcessing: false);
        }

        return Results.Text(
            "<!DOCTYPE html><html><head><title>404 Not Found</title></head><body><h1>404 Not Found</h1></body></html>",
            "text/html",
            statusCode: 404);
    }
}
