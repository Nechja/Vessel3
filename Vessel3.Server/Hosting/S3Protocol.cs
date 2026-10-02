using Vessel3.Server.Configuration;
using Vessel3.Server.Oidc;
using Vessel3.Server.Pipeline;
using Vessel3.Server.S3;
using Vessel3.Server.Telemetry;
using Vessel3.Storage;

namespace Vessel3.Server.Hosting;

internal sealed class S3Protocol : IVesselProtocol
{
    public string Name => "S3";

    public void ConfigureServices(IServiceCollection services, VesselConfig config)
    {
        services.AddSingleton<BucketXmlWriter>();
        services.AddSingleton<ObjectXmlWriter>();
        services.AddSingleton<S3ErrorXmlWriter>();
        services.AddSingleton<IBucketXmlWriter>(sp => sp.GetRequiredService<BucketXmlWriter>());
        services.AddSingleton<IObjectXmlWriter>(sp => sp.GetRequiredService<ObjectXmlWriter>());
        services.AddSingleton<IS3ErrorXmlWriter>(sp => sp.GetRequiredService<S3ErrorXmlWriter>());
        services.AddSingleton<S3XmlWriter>();
        services.AddSingleton<IS3XmlWriter>(sp => sp.GetRequiredService<S3XmlWriter>());

        services.AddSingleton<BucketXmlReader>();
        services.AddSingleton<ObjectXmlReader>();
        services.AddSingleton<IBucketXmlReader>(sp => sp.GetRequiredService<BucketXmlReader>());
        services.AddSingleton<IObjectXmlReader>(sp => sp.GetRequiredService<ObjectXmlReader>());
        services.AddSingleton<S3XmlReader>();
        services.AddSingleton<IS3XmlReader>(sp => sp.GetRequiredService<S3XmlReader>());
        services.AddSingleton<IHttpResultMapper, HttpResultMapper>();
        services.AddSingleton<IWebsiteService, WebsiteService>();
        services.AddSingleton<IMultipartStore, S3MultipartStore>();
        services.AddVesselS3Actions();
        services.AddSingleton<IS3SubresourceResolver, S3SubresourceResolver>();
        services.AddSingleton<IVirtualHostResolver, VirtualHostResolver>();
        services.AddSingleton<IS3BucketActionDispatcher, S3BucketActionDispatcher>();
        services.AddSingleton<IS3KeyActionDispatcher, S3KeyActionDispatcher>();

        AddS3Auth(services, config);
        AddS3Middlewares(services);
    }

    public void ConfigurePipeline(IApplicationBuilder app, VesselConfig config)
    {
        if (config.Oidc is not null)
        {
            app.UseMiddleware<StsEndpointMiddleware>();
        }

        app.UseMiddleware<VirtualHostBucketMiddleware>();
        app.UseMiddleware<CorsAndAccessMiddleware>();
        app.UseMiddleware<WebsiteServingMiddleware>();
        app.UseMiddleware<SigV4Middleware>();
        app.UseMiddleware<VirtualHostS3DispatchMiddleware>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", static async (HttpContext ctx, IS3XmlWriter xml, IBucketRegistry registry) =>
        {
            RequestTrace.SetAction("ListBuckets");
            ctx.Response.ContentType = "application/xml";
            var caller = ctx.GetCaller();
            var buckets = caller is not null ? registry.List(caller) : registry.List();
            await xml.WriteListBuckets(ctx.Response.Body, buckets, ctx.RequestAborted);
        });

        endpoints.MapGet("/{bucket}", static async (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Get, bucket, ctx);
            await result.ExecuteAsync(ctx);
        }).WithOrder(100);

        endpoints.MapPut("/{bucket}", static async (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Put, bucket, ctx);
            await result.ExecuteAsync(ctx);
        }).WithOrder(100);

        endpoints.MapDelete("/{bucket}", static async (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Delete, bucket, ctx);
            await result.ExecuteAsync(ctx);
        }).WithOrder(100);

        endpoints.MapPost("/{bucket}", static async (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Post, bucket, ctx);
            await result.ExecuteAsync(ctx);
        }).WithOrder(100);

        endpoints.MapMethods("/{bucket}", ["HEAD"], static async (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Head, bucket, ctx);
            await result.ExecuteAsync(ctx);
        }).WithOrder(100);

        endpoints.MapGet("/{bucket}/{**key}", static async (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Get, bucket, key, ctx);
            await result.ExecuteAsync(ctx);
        }).WithOrder(100);

        endpoints.MapPut("/{bucket}/{**key}", static async (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Put, bucket, key, ctx);
            await result.ExecuteAsync(ctx);
        }).WithOrder(100);

        endpoints.MapDelete("/{bucket}/{**key}", static async (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Delete, bucket, key, ctx);
            await result.ExecuteAsync(ctx);
        }).WithOrder(100);

        endpoints.MapPost("/{bucket}/{**key}", static async (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Post, bucket, key, ctx);
            await result.ExecuteAsync(ctx);
        }).WithOrder(100);

        endpoints.MapMethods("/{bucket}/{**key}", ["HEAD"], static async (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
        {
            var result = await dispatch.Dispatch(HttpMethods.Head, bucket, key, ctx);
            await result.ExecuteAsync(ctx);
        }).WithOrder(100);
    }

    private static void AddS3Auth(IServiceCollection services, VesselConfig config)
    {
        var rootCredential = config.AccessKey is not null && config.SecretKey is not null
            ? new Credential(config.AccessKey, config.SecretKey, SessionToken: null, ExpiresAt: null)
            : null;

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICredentialStore>(sp => new CredentialStore(
            rootCredential,
            sp.GetRequiredService<IIdentityRegistry>(),
            sp.GetRequiredService<TimeProvider>()));

        if (config.Oidc is not null)
        {
            services.AddSingleton(config.Oidc);
            services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
            services.AddSingleton<IOidcDiscovery, OidcDiscovery>();
            services.AddSingleton<ISigningKeys, JwksSigningKeys>();
            services.AddSingleton<ITokenVerifier, TokenVerifier>();
            services.AddSingleton<ISecurityTokenXmlWriter, SecurityTokenXmlWriter>();
            services.AddSingleton<ISecurityTokenService, SecurityTokenService>();
            services.AddSingleton<StsEndpointMiddleware>();
        }

        if (rootCredential is not null || config.Oidc is not null)
        {
            services.AddSingleton<ISigV4Verifier, SigV4Verifier>();
        }
        else
        {
            services.AddSingleton<ISigV4Verifier, AlwaysPassVerifier>();
        }
    }

    private static void AddS3Middlewares(IServiceCollection services)
    {
        services.AddSingleton<CorsAndAccessMiddleware>();
        services.AddSingleton<SigV4Middleware>();
        services.AddSingleton<VirtualHostBucketMiddleware>();
        services.AddSingleton<WebsiteServingMiddleware>();
        services.AddSingleton<VirtualHostS3DispatchMiddleware>();
    }
}
