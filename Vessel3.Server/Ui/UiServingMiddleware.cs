#if VESSEL3_UI
using System.Reflection;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Vessel3.Server.Admin;
using Vessel3.Server.Configuration;

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

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/_ui", out var remaining))
        {
            await next(context);
            return;
        }

        if (basicGate && !UiEndpoints.BasicAuthOk(context.Request.Headers.Authorization.ToString(), config.AccessKey!, config.SecretKey!))
        {
            context.Response.StatusCode = 401;
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"vessel3\"";
            return;
        }

        var relativePath = remaining.HasValue ? remaining.Value!.TrimStart('/') : "";

        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            if (relativePath == "config.json")
            {
                await ServeConfigAsync(context);
                return;
            }

            await ServeStaticAssetAsync(context, relativePath);
            return;
        }

        if (config.Oidc is not null && !verifier.Verify(context.Request).TryGetValue(out _, out var error))
        {
            await results.Map(error).ExecuteAsync(context);
            return;
        }

        if (await DispatchAdminActionAsync(context, relativePath))
        {
            return;
        }

        context.Response.StatusCode = 405;
    }

    private async Task ServeConfigAsync(HttpContext context)
    {
        var uiConfig = config.Oidc is null
            ? new UiConfig(config.AccessKey ?? "", config.SecretKey ?? "", config.Region, null)
            : await OidcConfig(context, config.Oidc, config.Region);
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await System.Text.Json.JsonSerializer.SerializeAsync(context.Response.Body, uiConfig, UiJsonContext.Default.UiConfig, context.RequestAborted);
    }

    private async Task ServeStaticAssetAsync(HttpContext context, string relativePath)
    {
        var path = string.IsNullOrEmpty(relativePath) ? "index.html" : relativePath;
        var fileInfo = assets.GetFileInfo(path);
        if (!fileInfo.Exists || fileInfo.IsDirectory)
        {
            if (UiEndpoints.IsAssetPath(path))
            {
                context.Response.StatusCode = 404;
                return;
            }
            fileInfo = assets.GetFileInfo("index.html");
        }

        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "no-cache";
        if (context.Request.Headers.IfNoneMatch.ToString() == etag)
        {
            context.Response.StatusCode = 304;
            return;
        }

        context.Response.ContentType = contentTypes.TryGetContentType(fileInfo.Name, out var contentType) ? contentType : "application/octet-stream";
        context.Response.ContentLength = fileInfo.Length;
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        await using var stream = fileInfo.CreateReadStream();
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private async Task<bool> DispatchAdminActionAsync(HttpContext context, string relativePath)
    {
        if (relativePath == "admin/gc" && HttpMethods.IsPost(context.Request.Method))
        {
            await adminService.RunGc(context);
            return true;
        }

        if (relativePath == "admin/lifecycle" && HttpMethods.IsPost(context.Request.Method))
        {
            await adminService.RunLifecycle(context);
            return true;
        }

        return false;
    }

    private async Task<UiConfig> OidcConfig(HttpContext context, OidcOptions oidc, string region)
    {
        var discovered = oidcDiscovery is not null ? await oidcDiscovery.Get(context.RequestAborted) : null;
        return new UiConfig("", "", region,
            new UiOidc(oidc.Issuer, oidc.ClientId, discovered?.AuthorizationEndpoint, discovered?.TokenEndpoint, discovered?.EndSessionEndpoint));
    }
}
#endif
