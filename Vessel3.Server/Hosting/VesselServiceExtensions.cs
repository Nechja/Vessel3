using Vessel3.Server.Admin;
using Vessel3.Server.Configuration;
using Vessel3.Server.Oidc;
using Vessel3.Server.Pipeline;
using Vessel3.Server.S3;
using Vessel3.Server.Telemetry;
#if VESSEL3_UI
using Vessel3.Server.Ui;
#endif
using Vessel3.Storage;
using Vessel3.Storage.Lifecycle;

namespace Vessel3.Server.Hosting;

internal static class VesselServiceExtensions
{
    public static IServiceCollection AddVessel(this IServiceCollection services, VesselConfig config)
    {
        services.AddVesselConfiguration(config);
        services.AddVesselStorage(config);
        services.AddVesselS3Protocol();
        services.AddVesselAuth(config);
        services.AddVesselTelemetry();
        services.AddVesselMiddlewares(config);
        return services;
    }

    private static void AddVesselConfiguration(this IServiceCollection services, VesselConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton(new ServerRegion(config.Region));
        services.AddSingleton(new VirtualHostOptions(config.BaseDomains));
        services.AddSingleton(new BlobPoolOptions(Path.Combine(config.DataRoot, "blobs")));
        services.AddSingleton(new BucketRegistryOptions(config.DataRoot));
        services.AddSingleton(new MultipartStoreOptions(Path.Combine(config.DataRoot, "uploads")));
        services.AddSingleton(new GcOptions(config.GcMaxWait, Path.Combine(config.DataRoot, "gc-tmp")));
        services.AddSingleton(new LifecycleServiceOptions(config.LifecycleInterval));
        services.AddSingleton(new CompactionServiceOptions(config.CompactInterval, config.CompactThresholdBytes));
        services.AddSingleton(new RequestTelemetryOptions(config.SlowRequestThreshold));
    }

    private static void AddVesselStorage(this IServiceCollection services, VesselConfig config)
    {
        services.AddSingleton<IFileSync>(OperatingSystem.IsLinux() ? new PosixFileSync() : new PortableFileSync());
        services.AddSingleton<IDurableWrite, DurableWrite>();
        services.AddSingleton<IBlobPool, BlobPool>();
        services.AddSingleton<IBucketRegistry, BucketRegistry>();
        services.AddSingleton<IObjectStore, ObjectStore>();
        services.AddSingleton<IMultipartStore, MultipartStore>();
        services.AddSingleton<IGcGate, GcGate>();
        services.AddSingleton<IGarbageCollector, GarbageCollector>();
        services.AddSingleton<ILifecycleSweeper, LifecycleSweeper>();
        services.AddHostedService<LifecycleService>();
        services.AddSingleton<ICompactor, Compactor>();
        services.AddHostedService<CompactionService>();
        services.AddSingleton<IBucketLister, BucketLister>();
        services.AddSingleton<IPreconditionEvaluator, PreconditionEvaluator>();
        services.AddSingleton<IAdminService, AdminService>();
    }

    private static void AddVesselS3Protocol(this IServiceCollection services)
    {
        services.AddSingleton<S3XmlWriter>();
        services.AddSingleton<IS3XmlWriter>(sp => sp.GetRequiredService<S3XmlWriter>());
        services.AddSingleton<IBucketXmlWriter>(sp => sp.GetRequiredService<S3XmlWriter>());
        services.AddSingleton<IObjectXmlWriter>(sp => sp.GetRequiredService<S3XmlWriter>());
        services.AddSingleton<IS3ErrorXmlWriter>(sp => sp.GetRequiredService<S3XmlWriter>());
        services.AddSingleton<IS3XmlReader, S3XmlReader>();
        services.AddSingleton<IHttpResultMapper, HttpResultMapper>();
        services.AddSingleton<IWebsiteService, WebsiteService>();
        services.AddVesselS3Actions();
        services.AddSingleton<IVirtualHostResolver, VirtualHostResolver>();
        services.AddSingleton<IS3BucketActionDispatcher, S3BucketActionDispatcher>();
        services.AddSingleton<IS3KeyActionDispatcher, S3KeyActionDispatcher>();
    }

    private static void AddVesselAuth(this IServiceCollection services, VesselConfig config)
    {
        var rootCredential = config.AccessKey is not null && config.SecretKey is not null
            ? new Credential(config.AccessKey, config.SecretKey, SessionToken: null, ExpiresAt: null)
            : null;

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICredentialStore>(sp => new CredentialStore(rootCredential, sp.GetRequiredService<TimeProvider>()));

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

    private static void AddVesselTelemetry(this IServiceCollection services)
    {
        services.AddSingleton<MetricsService>();
        services.AddSingleton<IMetricsCollector>(sp => sp.GetRequiredService<MetricsService>());
        services.AddSingleton<IMetricsRenderer>(sp => sp.GetRequiredService<MetricsService>());
        services.AddSingleton<IMetricsService>(sp => sp.GetRequiredService<MetricsService>());
        services.AddSingleton(sp => new BucketStatsCache(
            sp.GetRequiredService<IBucketRegistry>(),
            sp.GetRequiredService<TimeProvider>(),
            TimeSpan.FromSeconds(60)));
    }

    private static void AddVesselMiddlewares(this IServiceCollection services, VesselConfig config)
    {
        services.AddSingleton<RequestTelemetry>();
        services.AddSingleton<CorsAndAccessMiddleware>();
        services.AddSingleton<SigV4Middleware>();
        services.AddSingleton<MetricsEndpointMiddleware>();
        services.AddSingleton<AdminHostRedirectMiddleware>();
        services.AddSingleton<VirtualHostBucketMiddleware>();
        services.AddSingleton<WebsiteServingMiddleware>();
        services.AddSingleton<VirtualHostS3DispatchMiddleware>();
#if VESSEL3_UI
        services.AddSingleton<UiServingMiddleware>();
#endif
    }
}
