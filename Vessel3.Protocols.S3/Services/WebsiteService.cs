using System.Globalization;

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
    public async Task<IResult> Serve(string bucket, string rawPath, HttpContext ctx)
    {
        if (registry.GetWebsite(bucket) is not Result<WebsiteConfig?>.Success { Value: { } cfg })
            return Results.StatusCode(404);

        var path = (rawPath ?? "/").TrimStart('/');
        if (TryResolveDirectoryRedirect(bucket, path, cfg, ctx, out var redirectResult))
        {
            return redirectResult;
        }

        var targetKey = ResolveTargetKey(path, cfg);

        return HttpMethods.IsHead(ctx.Request.Method)
            ? ServeHead(bucket, targetKey, cfg, ctx)
            : await ServeGet(bucket, targetKey, cfg, ctx, ctx.RequestAborted);
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
            var readPre = S3HeaderCodec.ExtractReadPreconditions(ctx.Request.Headers);
            var precond = preconditions.Evaluate(readPre, stat.Etag, stat.LastModified);
            if (precond is Precondition.NotModified) return Results.StatusCode(304);
            if (precond is Precondition.Failed) return Results.StatusCode(412);

            ctx.Response.ContentLength = stat.Size;
            ctx.Response.ContentType = ResolveContentType(targetKey, stat.ContentType);
            ctx.Response.Headers.ETag = $"\"{stat.Etag}\"";
            ctx.Response.Headers.LastModified = stat.LastModified.ToString("R", CultureInfo.InvariantCulture);
            S3HeaderCodec.EmitSystemHeaders(ctx.Response.Headers, stat.SystemHeaders);
            return Results.Empty;
        }

        if (!string.IsNullOrEmpty(cfg.ErrorDocument)
            && objects.Stat(bucket, cfg.ErrorDocument, versionId: null) is Result<ObjectStat>.Success { Value: var errStat })
        {
            ctx.Response.StatusCode = string.Equals(cfg.ErrorDocument, cfg.IndexDocument, StringComparison.OrdinalIgnoreCase)
                ? StatusCodes.Status200OK
                : StatusCodes.Status404NotFound;
            ctx.Response.ContentLength = errStat.Size;
            ctx.Response.ContentType = ResolveContentType(cfg.ErrorDocument, errStat.ContentType);
            return Results.Empty;
        }

        return Results.StatusCode(404);
    }

    private async Task<IResult> ServeGet(
        string bucket,
        string targetKey,
        WebsiteConfig cfg,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (await objects.Get(bucket, targetKey, versionId: null, ct) is Result<StoredObject>.Success { Value: var obj })
        {
            var readPre = S3HeaderCodec.ExtractReadPreconditions(ctx.Request.Headers);
            var precond = preconditions.Evaluate(readPre, obj.Etag, obj.LastModified);
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
            S3HeaderCodec.EmitSystemHeaders(ctx.Response.Headers, obj.SystemHeaders);

            var contentType = ResolveContentType(targetKey, obj.ContentType);
            return Results.File(
                obj.Body,
                contentType,
                lastModified: obj.LastModified,
                enableRangeProcessing: true);
        }

        return await ServeErrorDocumentOrDefault(bucket, cfg, ctx, ct);
    }

    private async Task<IResult> ServeErrorDocumentOrDefault(
        string bucket,
        WebsiteConfig cfg,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(cfg.ErrorDocument)
            && await objects.Get(bucket, cfg.ErrorDocument, versionId: null, ct) is Result<StoredObject>.Success { Value: var errObj })
        {
            ctx.Response.StatusCode = string.Equals(cfg.ErrorDocument, cfg.IndexDocument, StringComparison.OrdinalIgnoreCase)
                ? StatusCodes.Status200OK
                : StatusCodes.Status404NotFound;
            ctx.Response.Headers.ETag = $"\"{errObj.Etag}\"";
            S3HeaderCodec.EmitSystemHeaders(ctx.Response.Headers, errObj.SystemHeaders);

            var errContentType = ResolveContentType(cfg.ErrorDocument, errObj.ContentType);
            return Results.File(
                errObj.Body,
                errContentType,
                lastModified: errObj.LastModified,
                enableRangeProcessing: false);
        }

        return Results.Text(
            "<!DOCTYPE html><html><head><title>404 Not Found</title></head><body><h1>404 Not Found</h1></body></html>",
            "text/html",
            statusCode: 404);
    }

    private static readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider ContentTypeProvider = new();

    private static string ResolveContentType(string key, string? storedContentType) =>
        !string.IsNullOrEmpty(storedContentType) && !string.Equals(storedContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? storedContentType
            : ContentTypeProvider.TryGetContentType(key, out var inferred) ? inferred
            : key.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase) ? "application/wasm"
            : key.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase) ? "application/manifest+json"
            : storedContentType ?? "application/octet-stream";
}
