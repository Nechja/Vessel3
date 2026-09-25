using System.Globalization;
using Vessel3.Server.Storage;
using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3;

internal static class WebsiteHandler
{
    public static async Task<IResult> Serve(
        string bucket,
        string rawPath,
        HttpContext ctx,
        IObjectStore objects,
        IBucketRegistry registry,
        IPreconditionEvaluator pre)
    {
        if (registry.GetWebsite(bucket) is not Result<WebsiteConfig?>.Success { Value: { } cfg })
            return Results.StatusCode(404);

        var path = (rawPath ?? "/").TrimStart('/');
        string targetKey;

        if (string.IsNullOrEmpty(path))
        {
            targetKey = cfg.IndexDocument;
        }
        else if (path.EndsWith('/'))
        {
            targetKey = $"{path}{cfg.IndexDocument}";
        }
        else
        {
            // Check if exact path exists as an object
            if (objects.Stat(bucket, path, versionId: null) is Result<ObjectStat>.Success)
            {
                targetKey = path;
            }
            // Check if path is a directory containing IndexDocument -> 301 redirect with trailing slash
            else if (objects.Stat(bucket, $"{path}/{cfg.IndexDocument}", versionId: null) is Result<ObjectStat>.Success)
            {
                var query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : string.Empty;
                return Results.Redirect($"/{path}/{query}", permanent: true);
            }
            else
            {
                targetKey = path;
            }
        }

        if (HttpMethods.IsHead(ctx.Request.Method))
        {
            if (objects.Stat(bucket, targetKey, versionId: null) is Result<ObjectStat>.Success { Value: var stat })
            {
                var precond = pre.EvaluateForRead(ctx.Request.Headers, stat.Etag, stat.LastModified);
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

        // GET
        if (objects.Get(bucket, targetKey, versionId: null) is Result<StoredObject>.Success { Value: var obj })
        {
            var precond = pre.EvaluateForRead(ctx.Request.Headers, obj.Etag, obj.LastModified);
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

        // 404: Try ErrorDocument
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
