#if VESSEL3_UI
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Vessel3.Server.Oidc;
using Vessel3.Server.S3;

namespace Vessel3.Server.Ui;

internal static class UiEndpoints
{
    public static void UseVessel3Ui(this WebApplication app, string? accessKey, string? secretKey, string region, OidcOptions? oidc)
    {
        var assets = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
        var contentTypes = new FileExtensionContentTypeProvider();
        var etag = $"\"{Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId:N}\"";
        var basicGate = oidc is null && accessKey is not null && secretKey is not null;
        var verifier = app.Services.GetRequiredService<ISigV4Verifier>();
        var results = app.Services.GetRequiredService<IHttpResultMapper>();

        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/_ui", out var remaining))
            {
                await next(ctx);
                return;
            }

            if (basicGate && !BasicAuthOk(ctx.Request.Headers.Authorization.ToString(), accessKey!, secretKey!))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"vessel3\"";
                return;
            }

            var rel = remaining.HasValue ? remaining.Value!.TrimStart('/') : "";

            if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
            {
                if (rel == "config.json")
                {
                    var config = oidc is null
                        ? new UiConfig(accessKey ?? "", secretKey ?? "", region, null)
                        : await OidcConfig(ctx, oidc, region);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.Headers.CacheControl = "no-store";
                    await JsonSerializer.SerializeAsync(ctx.Response.Body, config, UiJsonContext.Default.UiConfig, ctx.RequestAborted);
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
                return;
            }

            if (oidc is not null && !verifier.Verify(ctx.Request).TryGetValue(out _, out var err))
            {
                await results.Map(err).ExecuteAsync(ctx);
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

            ctx.Response.StatusCode = 405;
        });
    }

    private static async Task<UiConfig> OidcConfig(HttpContext ctx, OidcOptions oidc, string region)
    {
        var discovered = await ctx.RequestServices.GetRequiredService<IOidcDiscovery>().Get(ctx.RequestAborted);
        return new UiConfig("", "", region,
            new UiOidc(oidc.Issuer, oidc.ClientId, discovered?.AuthorizationEndpoint, discovered?.TokenEndpoint, discovered?.EndSessionEndpoint));
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

    // SPA routes never live under the framework dirs and never put a dot in a
    // top-level segment; bucket keys with dots sit under buckets/ and still fall back.
    public static bool IsAssetPath(string rel) =>
        rel.StartsWith("_framework/", StringComparison.Ordinal)
        || rel.StartsWith("_content/", StringComparison.Ordinal)
        || (!rel.Contains('/', StringComparison.Ordinal) && rel.Contains('.', StringComparison.Ordinal));
}
#endif
