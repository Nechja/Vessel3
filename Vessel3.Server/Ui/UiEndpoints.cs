#if VESSEL3_UI
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Vessel3.Server.Storage;

namespace Vessel3.Server.Ui;

internal static class UiEndpoints
{
    public static void UseVessel3Ui(this WebApplication app, string? accessKey, string? secretKey, string region)
    {
        var assets = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
        var contentTypes = new FileExtensionContentTypeProvider();
        var config = new UiConfig(accessKey ?? "", secretKey ?? "", region);
        var etag = $"\"{Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId:N}\"";

        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/_ui", out var remaining))
            {
                await next(ctx);
                return;
            }

            if (accessKey is not null && secretKey is not null
                && !BasicAuthOk(ctx.Request.Headers.Authorization.ToString(), accessKey, secretKey))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"vessel3\"";
                return;
            }

            var rel = remaining.HasValue ? remaining.Value!.TrimStart('/') : "";

            if (rel == "config.json")
            {
                ctx.Response.ContentType = "application/json";
                ctx.Response.Headers.CacheControl = "no-store";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, config, UiJsonContext.Default.UiConfig, ctx.RequestAborted);
                return;
            }

            if (rel == "admin/gc" && HttpMethods.IsPost(ctx.Request.Method))
            {
                await AdminEndpoints.RunGc(ctx);
                return;
            }

            if (rel == "admin/lifecycle" && HttpMethods.IsPost(ctx.Request.Method))
            {
                await AdminEndpoints.RunLifecycle(ctx);
                return;
            }

            if (rel.StartsWith("upload/", StringComparison.Ordinal) && HttpMethods.IsPut(ctx.Request.Method))
            {
                await HandleUpload(ctx, rel["upload/".Length..]);
                return;
            }

            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
            {
                ctx.Response.StatusCode = 405;
                return;
            }

            if (string.IsNullOrEmpty(rel)) rel = "index.html";
            var info = assets.GetFileInfo(rel);
            if (!info.Exists || info.IsDirectory)
            {
                if (IsAssetPath(rel))
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }
                info = assets.GetFileInfo("index.html");
            }

            ctx.Response.Headers.ETag = etag;
            ctx.Response.Headers.CacheControl = "no-cache";
            if (ctx.Request.Headers.IfNoneMatch.ToString() == etag)
            {
                ctx.Response.StatusCode = 304;
                return;
            }

            ctx.Response.ContentType = contentTypes.TryGetContentType(info.Name, out var ct) ? ct : "application/octet-stream";
            ctx.Response.ContentLength = info.Length;
            if (HttpMethods.IsHead(ctx.Request.Method)) return;
            await using var stream = info.CreateReadStream();
            await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        });
    }

    private static async Task HandleUpload(HttpContext ctx, string path)
    {
        if (!TryParseUploadPath(path, out var bucket, out var key))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("upload path: /_ui/upload/{bucket}/{key}", ctx.RequestAborted);
            return;
        }

        var objects = ctx.RequestServices.GetRequiredService<IObjectStore>();
        var contentType = ctx.Request.ContentType ?? "application/octet-stream";
        var put = await objects.Put(bucket, key, ctx.Request.Body, ctx.Request.ContentLength, contentType,
            declaredSha256: null, declaredMd5Base64: null,
            metadata: new Dictionary<string, string>(),
            tags: new Dictionary<string, string>(),
            declaredChecksums: ChecksumSet.Empty,
            ct: ctx.RequestAborted);
        if (!put.TryGetValue(out var outcome, out var err))
        {
            ctx.Response.StatusCode = err.Status;
            await ctx.Response.WriteAsync(err.Message, ctx.RequestAborted);
            return;
        }
        ctx.Response.Headers.ETag = $"\"{outcome.Etag}\"";
        if (!string.IsNullOrEmpty(outcome.VersionId)) ctx.Response.Headers["x-amz-version-id"] = outcome.VersionId;
        ctx.Response.StatusCode = 200;
    }

    public static bool BasicAuthOk(string authorization, string accessKey, string secretKey)
    {
        const string scheme = "Basic ";
        if (!authorization.StartsWith(scheme, StringComparison.Ordinal)) return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[scheme.Length..]));
            var colon = decoded.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) return false;
            var userOk = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(decoded[..colon]), Encoding.UTF8.GetBytes(accessKey));
            var passOk = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(decoded[(colon + 1)..]), Encoding.UTF8.GetBytes(secretKey));
            return userOk & passOk;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool TryParseUploadPath(string path, out string bucket, out string key)
    {
        bucket = string.Empty;
        key = string.Empty;
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == path.Length - 1) return false;
        bucket = path[..slash];
        key = path[(slash + 1)..];
        return true;
    }

    // SPA routes never live under the framework dirs and never put a dot in a
    // top-level segment; bucket keys with dots sit under buckets/ and still fall back.
    public static bool IsAssetPath(string rel) =>
        rel.StartsWith("_framework/", StringComparison.Ordinal)
        || rel.StartsWith("_content/", StringComparison.Ordinal)
        || (!rel.Contains('/', StringComparison.Ordinal) && rel.Contains('.', StringComparison.Ordinal));
}
#endif
