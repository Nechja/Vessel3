using Vessel3.Server.Admin;
using Vessel3.Server.Configuration;
using Vessel3.Server.Pipeline;
using Vessel3.Server.Telemetry;
#if VESSEL3_UI
using Vessel3.Server.Ui;
#endif

namespace Vessel3.Server.Hosting;

internal static class VesselServiceExtensions
{
    public static IServiceCollection AddVessel(this IServiceCollection services, VesselConfig config)
    {
        services.AddVesselConfiguration(config);
        services.AddVesselStorage(config);
        services.AddVesselTelemetry();
        services.AddVesselOpenTelemetry(config);
        services.AddVesselHostMiddlewares();
        services.AddVesselProtocols(config);
        return services;
    }

    private static void AddVesselConfiguration(this IServiceCollection services, VesselConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton(new ServerRegion(config.Region));
        services.AddSingleton(new VirtualHostOptions(config.BaseDomains));
        services.AddSingleton(new BucketRegistryOptions(config.DataRoot));
        services.AddSingleton(new ChunkStagerOptions(Path.Combine(config.DataRoot, "uploads")));
        services.AddSingleton(new GcOptions(config.GcMaxWait, Path.Combine(config.DataRoot, "gc-tmp")));
        services.AddSingleton(new LifecycleServiceOptions(config.LifecycleInterval));
        services.AddSingleton(new CompactionServiceOptions(config.CompactInterval, config.CompactThresholdBytes));
        services.AddSingleton(new RequestTelemetryOptions(config.SlowRequestThreshold, config.AccessLogEnabled));
        services.AddSingleton(new IdentityOptions(Path.Combine(config.DataRoot, "iam")));
        services.AddSingleton(new WebhookStoreOptions(Path.Combine(config.DataRoot, "webhooks")));
    }

    private static void AddVesselStorage(this IServiceCollection services, VesselConfig config)
    {
        services.AddSingleton<IFileSync>(OperatingSystem.IsLinux() ? new PosixFileSync() : new PortableFileSync());
        services.AddSingleton<IDurableWrite, DurableWrite>();

        var volumes = config.Volumes ?? [StorageVolume.CreateDefault(config.DataRoot)];
        if (volumes.Count <= 1)
        {
            services.AddSingleton<IBlobLocationCatalog>(NullBlobLocationCatalog.Instance);
        }
        else
        {
            services.AddSingleton<IBlobLocationCatalog, MemoryBlobLocationCatalog>();
        }

        services.AddSingleton<IVolumeRegistry>(sp => new VolumeRegistry(volumes, sp.GetRequiredService<IFileSync>()));
        services.AddSingleton<IBlobPool>(sp => new BlobPool(
            sp.GetRequiredService<IVolumeRegistry>(),
            sp.GetRequiredService<IBlobLocationCatalog>()));
        services.AddSingleton<IBucketRegistry, BucketRegistry>();
        services.AddSingleton<IBlobReferenceSource>(sp => sp.GetRequiredService<IBucketRegistry>());
        services.AddSingleton<IIdentityRegistry>(sp =>
        {
            var options = sp.GetRequiredService<IdentityOptions>();
            var clock = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var reg = new IdentityRegistry(options, clock, sp.GetService<IWebhookEventPublisher>());
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

        services.AddSingleton<IWebhookStore>(sp =>
        {
            var options = sp.GetRequiredService<WebhookStoreOptions>();
            var clock = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var store = new SqliteWebhookStore(options, clock);

            var yamlPath = config.WebhooksFile ?? Path.Combine(config.DataRoot, "webhooks.yaml");
            if (File.Exists(yamlPath))
            {
                var yamlContent = File.ReadAllText(yamlPath);
                var loaded = YamlWebhookLoader.LoadFromYaml(yamlContent, clock);
                if (loaded.TryGetValue(out var staticWebhooks, out _))
                {
                    foreach (var sw in staticWebhooks)
                    {
                        store.UpsertStaticWebhook(sw);
                    }
                }
            }

            return store;
        });

        services.AddSingleton<IEventStreamHub, EventStreamHub>();
        services.AddSingleton<IServerLogBuffer, ServerLogBuffer>();
        services.AddSingleton<WebhookDeliveryWorker>(sp => new WebhookDeliveryWorker(
            sp.GetRequiredService<IWebhookStore>(),
            new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) },
            sp.GetRequiredService<ILogger<WebhookDeliveryWorker>>(),
            sp.GetService<TimeProvider>(),
            sp.GetRequiredService<IEventStreamHub>()));

        services.AddSingleton<IWebhookEventPublisher>(sp => sp.GetRequiredService<WebhookDeliveryWorker>());
        services.AddSingleton<IWebhookDeliveryService>(sp => sp.GetRequiredService<WebhookDeliveryWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<WebhookDeliveryWorker>());
    }

    private static void AddVesselProtocols(this IServiceCollection services, VesselConfig config)
    {
        var azure = new AzureProtocol();
        services.AddSingleton<IVesselProtocol>(azure);
        azure.ConfigureServices(services, config);

        if (config.WebDavEnabled)
        {
            var webdav = new WebDavProtocol();
            services.AddSingleton<IVesselProtocol>(webdav);
            webdav.ConfigureServices(services, config);
        }

        var s3 = new S3Protocol();
        services.AddSingleton<IVesselProtocol>(s3);
        s3.ConfigureServices(services, config);

        var native = new NativeProtocol();
        services.AddSingleton<IVesselProtocol>(native);
        native.ConfigureServices(services, config);

        if (config.OciEnabled)
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
        services.AddSingleton<ProbeEndpointMiddleware>();
        services.AddSingleton<RequestTelemetry>();
        services.AddSingleton<MetricsEndpointMiddleware>();
        services.AddSingleton<AdminHostRedirectMiddleware>();
#if VESSEL3_UI
        services.AddSingleton<UiServingMiddleware>();
#endif
    }
}
