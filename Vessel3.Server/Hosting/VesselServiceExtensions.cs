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
        services.AddVesselTelemetry();
        services.AddVesselHostMiddlewares();
        services.AddVesselProtocols(config);
        return services;
    }

    private static void AddVesselConfiguration(this IServiceCollection services, VesselConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton(new ServerRegion(config.Region));
        services.AddSingleton(new VirtualHostOptions(config.BaseDomains));
        services.AddSingleton(new BlobPoolOptions(Path.Combine(config.DataRoot, "blobs")));
        services.AddSingleton(new BucketRegistryOptions(config.DataRoot));
        services.AddSingleton(new ChunkStagerOptions(Path.Combine(config.DataRoot, "uploads")));
        services.AddSingleton(new GcOptions(config.GcMaxWait, Path.Combine(config.DataRoot, "gc-tmp")));
        services.AddSingleton(new LifecycleServiceOptions(config.LifecycleInterval));
        services.AddSingleton(new CompactionServiceOptions(config.CompactInterval, config.CompactThresholdBytes));
        services.AddSingleton(new RequestTelemetryOptions(config.SlowRequestThreshold));
        services.AddSingleton(new IdentityOptions(Path.Combine(config.DataRoot, "iam")));
    }

    private static void AddVesselStorage(this IServiceCollection services, VesselConfig config)
    {
        services.AddSingleton<IFileSync>(OperatingSystem.IsLinux() ? new PosixFileSync() : new PortableFileSync());
        services.AddSingleton<IDurableWrite, DurableWrite>();
        services.AddSingleton<IBlobPool, BlobPool>();
        services.AddSingleton<IBucketRegistry, BucketRegistry>();
        services.AddSingleton<IBlobReferenceSource>(sp => sp.GetRequiredService<IBucketRegistry>());
        services.AddSingleton<IIdentityRegistry>(sp =>
        {
            var options = sp.GetRequiredService<IdentityOptions>();
            var clock = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var reg = new IdentityRegistry(options, clock);
            if (config.AccessKey is not null && config.SecretKey is not null)
                reg.EnsureBootstrapAdmin(config.AccessKey, config.SecretKey);
            if (config.AdminUsers is { Count: > 0 } admins)
                reg.EnsureAdminUsers(admins);
            return reg;
        });
        services.AddSingleton<IObjectStore, ObjectStore>();
        services.AddSingleton<IChunkStager, ChunkStager>();
        services.AddSingleton<IGcGate, GcGate>();
        services.AddSingleton<IGarbageCollector>(sp => new GarbageCollector(
            sp.GetRequiredService<IBlobPool>(),
            sp.GetServices<IBlobReferenceSource>(),
            sp.GetRequiredService<IChunkStager>(),
            sp.GetRequiredService<IGcGate>(),
            sp.GetRequiredService<GcOptions>()));
        services.AddSingleton<ILifecycleSweeper, LifecycleSweeper>();
        services.AddHostedService<LifecycleService>();
        services.AddSingleton<ICompactor, Compactor>();
        services.AddHostedService<CompactionService>();
        services.AddSingleton<IBucketLister, BucketLister>();
        services.AddSingleton<IPreconditionEvaluator, PreconditionEvaluator>();
        services.AddSingleton<IAdminService, AdminService>();
    }

    private static void AddVesselProtocols(this IServiceCollection services, VesselConfig config)
    {
        var s3 = new S3Protocol();
        services.AddSingleton<IVesselProtocol>(s3);
        s3.ConfigureServices(services, config);

        var native = new NativeProtocol();
        services.AddSingleton<IVesselProtocol>(native);
        native.ConfigureServices(services, config);

        if (config.ContainerReposEnabled)
        {
            var oci = new OciProtocol();
            services.AddSingleton<IVesselProtocol>(oci);
            oci.ConfigureServices(services, config);
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

    private static void AddVesselHostMiddlewares(this IServiceCollection services)
    {
        services.AddSingleton<RequestTelemetry>();
        services.AddSingleton<MetricsEndpointMiddleware>();
        services.AddSingleton<AdminHostRedirectMiddleware>();
#if VESSEL3_UI
        services.AddSingleton<UiServingMiddleware>();
#endif
    }
}
