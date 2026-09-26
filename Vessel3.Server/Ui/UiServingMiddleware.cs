#if VESSEL3_UI
using System.Reflection;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Vessel3.Server.Admin;
using Vessel3.Server.Configuration;
using Vessel3.Server.Oidc;
using Vessel3.Server.S3;

namespace Vessel3.Server.Ui;

internal sealed class UiServingMiddleware(
    VesselConfig config,
    ISigV4Verifier verifier,
    IHttpResultMapper results,
    IAdminService adminService,
    IOidcDiscovery? oidcDiscovery = null) : IMiddleware
{
    private readonly ManifestEmbeddedFileProvider assets = new(Assembly.GetExecutingAssembly(), "wwwroot");
    private readonly FileExtensionContentTypeProvider contentTypes = new();
    private readonly string etag = $"\"{Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId:N}\"";
    private readonly bool basicGate = config.Oidc is null && config.AccessKey is not null && config.SecretKey is not null;

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (!ctx.Request.Path.StartsWithSegments("/_ui", out var remaining))
        {
            await next(ctx);
            return;
        }

        if (basicGate && !UiEndpoints.BasicAuthOk(ctx.Request.Headers.Authorization.ToString(), config.AccessKey!, config.SecretKey!))
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
                var uiConfig = config.Oidc is null
                    ? new UiConfig(config.AccessKey ?? "", config.SecretKey ?? "", config.Region, null)
                    : await OidcConfig(ctx, config.Oidc, config.Region);
                ctx.Response.ContentType = "application/json";
                ctx.Response.Headers.CacheControl = "no-store";
                await System.Text.Json.JsonSerializer.SerializeAsync(ctx.Response.Body, uiConfig, UiJsonContext.Default.UiConfig, ctx.RequestAborted);
                return;
            }

            if (string.IsNullOrEmpty(rel)) rel = "index.html";
            var info = assets.GetFileInfo(rel);
            if (!info.Exists || info.IsDirectory)
            {
                if (UiEndpoints.IsAssetPath(rel))
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

        if (config.Oidc is not null && !verifier.Verify(ctx.Request).TryGetValue(out _, out var err))
        {
            await results.Map(err).ExecuteAsync(ctx);
            return;
        }

        if (rel == "admin/gc" && HttpMethods.IsPost(ctx.Request.Method))
        {
            await adminService.RunGc(ctx);
            return;
        }

        if (rel == "admin/lifecycle" && HttpMethods.IsPost(ctx.Request.Method))
        {
            await adminService.RunLifecycle(ctx);
            return;
        }

        ctx.Response.StatusCode = 405;
    }

    private async Task<UiConfig> OidcConfig(HttpContext ctx, OidcOptions oidc, string region)
    {
        var discovered = oidcDiscovery is not null ? await oidcDiscovery.Get(ctx.RequestAborted) : null;
        return new UiConfig("", "", region,
            new UiOidc(oidc.Issuer, oidc.ClientId, discovered?.AuthorizationEndpoint, discovered?.TokenEndpoint, discovered?.EndSessionEndpoint));
    }
}
#endif
