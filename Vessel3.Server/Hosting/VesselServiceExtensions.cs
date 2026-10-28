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
        var resolvedNodeId = config.NodeId ?? config.Cluster?.NodeId ?? Environment.MachineName;
        services.AddSingleton(config);
        services.AddSingleton(new ServerRegion(config.Region));
        services.AddSingleton(new VirtualHostOptions(config.BaseDomains));
        services.AddSingleton(new BucketRegistryOptions(config.DataRoot, resolvedNodeId));
        services.AddSingleton(new ChunkStagerOptions(Path.Combine(config.DataRoot, VesselDefaults.UploadsSubdirectory)));
        services.AddSingleton(new GcOptions(config.GcMaxWait, Path.Combine(config.DataRoot, VesselDefaults.GcTmpSubdirectory)));
        services.AddSingleton(new LifecycleServiceOptions(config.LifecycleInterval));
        services.AddSingleton(new CompactionServiceOptions(config.CompactInterval, config.CompactThresholdBytes));
        services.AddSingleton(new RequestTelemetryOptions(config.SlowRequestThreshold, config.AccessLogEnabled));
        services.AddSingleton(new IdentityOptions(Path.Combine(config.DataRoot, VesselDefaults.IamSubdirectory)));
        services.AddSingleton(new WebhookStoreOptions(Path.Combine(config.DataRoot, VesselDefaults.WebhooksSubdirectory)));
        services.AddSingleton(config.Cluster ?? new ClusterMembershipOptions(
            resolvedNodeId,
            string.Empty,
            [],
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            config.SecretKey));
    }

    private static void AddVesselStorage(this IServiceCollection services, VesselConfig config)
    {
        services.AddSingleton<IFileSync>(OperatingSystem.IsLinux() ? new PosixFileSync() : new PortableFileSync());
        services.AddSingleton<IDurableWrite, DurableWrite>();

        var resolvedNodeId = config.NodeId ?? config.Cluster?.NodeId ?? Environment.MachineName;
        var isClusterEnabled = config.Cluster?.IsClusterEnabled ?? false;
        var volumes = config.Volumes ?? [StorageVolume.CreateDefault(config.DataRoot, resolvedNodeId, isClusterEnabled)];
        services.AddSingleton<IBlobLocationCatalog, MemoryBlobLocationCatalog>();
        services.AddSingleton<IBlobMirrorQueue, BlobMirrorQueue>();
        services.AddSingleton(static _ => new ClusterHttpClients());
        services.AddSingleton<Func<StorageVolume, IBlobReader>>(sp =>
        {
            var clients = sp.GetRequiredService<ClusterHttpClients>();
            var secret = sp.GetRequiredService<ClusterMembershipOptions>().ClusterSecret;
            return volume => new HttpRemoteBlobReader(volume.Id, volume.Path, clients.BlobClient, secret);
        });
        services.AddSingleton<IVolumeRegistry>(sp => new VolumeRegistry(
            volumes,
            sp.GetRequiredService<IFileSync>(),
            remoteFactory: sp.GetRequiredService<Func<StorageVolume, IBlobReader>>()));
        services.AddSingleton<IBlobPool>(sp => new BlobPool(
            sp.GetRequiredService<IVolumeRegistry>(),
            sp.GetRequiredService<IBlobLocationCatalog>(),
            sp.GetRequiredService<IBlobMirrorQueue>()));
        services.AddSingleton<IClusterMembership>(sp => new ClusterMembership(
            sp.GetRequiredService<ClusterMembershipOptions>(),
            sp.GetRequiredService<IVolumeRegistry>(),
            sp.GetService<TimeProvider>()));
        services.AddSingleton<IGossipTransport>(sp => new HttpGossipTransport(
            sp.GetRequiredService<ClusterHttpClients>().GossipClient,
            sp.GetRequiredService<ClusterMembershipOptions>().ClusterSecret));
        services.AddSingleton<HttpClusterReplicationTransport>(sp => new HttpClusterReplicationTransport(
            sp.GetRequiredService<ClusterHttpClients>().BlobClient,
            sp.GetRequiredService<IClusterMembership>(),
            sp.GetRequiredService<ClusterMembershipOptions>().ClusterSecret));
        services.AddSingleton<IClusterReplicationTransport>(sp => sp.GetRequiredService<HttpClusterReplicationTransport>());
        services.AddSingleton<IClusterUploadResolver>(sp => sp.GetRequiredService<HttpClusterReplicationTransport>());
        services.AddSingleton<GossipService>(sp => new GossipService(
            sp.GetRequiredService<ClusterMembershipOptions>(),
            sp.GetRequiredService<IClusterMembership>(),
            sp.GetRequiredService<IGossipTransport>(),
            sp.GetService<ILogger<GossipService>>()));
        services.AddHostedService(sp => sp.GetRequiredService<GossipService>());
        services.AddSingleton<VolumeMirrorService>(sp => new VolumeMirrorService(
            sp.GetRequiredService<ClusterMembershipOptions>(),
            sp.GetRequiredService<IVolumeRegistry>(),
            sp.GetRequiredService<IBlobMirrorQueue>(),
            sp.GetService<ILogger<VolumeMirrorService>>()));
        services.AddHostedService(sp => sp.GetRequiredService<VolumeMirrorService>());
        services.AddSingleton<IBucketRegistry, BucketRegistry>();
        services.AddSingleton<IBlobReferenceSource>(sp => sp.GetRequiredService<IBucketRegistry>());
        services.AddSingleton<IBlobReferenceSource, ClusterBlobReferenceSource>();
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
        services.AddSingleton<ClusterReplicationService>(sp => new ClusterReplicationService(
            sp.GetRequiredService<ClusterMembershipOptions>(),
            sp.GetRequiredService<IClusterMembership>(),
            sp.GetRequiredService<IBucketRegistry>(),
            sp.GetRequiredService<IIdentityRegistry>(),
            sp.GetRequiredService<IVolumeRegistry>(),
            sp.GetRequiredService<IBlobMirrorQueue>(),
            sp.GetRequiredService<VolumeMirrorService>(),
            sp.GetRequiredService<IClusterReplicationTransport>(),
            sp.GetService<ILogger<ClusterReplicationService>>()));
        services.AddHostedService(sp => sp.GetRequiredService<ClusterReplicationService>());
        services.AddSingleton<IObjectStore, ObjectStore>();
        services.AddSingleton<IChunkStager>(sp => new ChunkStager(
            sp.GetRequiredService<ChunkStagerOptions>(),
            sp.GetRequiredService<IBucketRegistry>(),
            sp.GetRequiredService<IBlobPool>(),
            sp.GetRequiredService<IDurableWrite>(),
            sp.GetRequiredService<IGcGate>(),
            sp.GetRequiredService<IChecksumValidator>(),
            sp.GetRequiredService<IClusterUploadResolver>(),
            sp.GetRequiredService<IBlobMirrorQueue>()));
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
        services.AddSingleton<IChecksumValidator, ChecksumValidator>();
        services.AddSingleton<IWebhookYamlSerializer, WebhookYamlSerializer>();
        services.AddSingleton<IPreconditionEvaluator, PreconditionEvaluator>();
        services.AddSingleton<IAdminService, AdminService>();

        services.AddSingleton<IWebhookStore>(sp =>
        {
            var options = sp.GetRequiredService<WebhookStoreOptions>();
            var clock = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var serializer = sp.GetRequiredService<IWebhookYamlSerializer>();
            var store = new SqliteWebhookStore(options, clock);

            var yamlPath = config.WebhooksFile ?? Path.Combine(config.DataRoot, VesselDefaults.WebhooksDefaultFileName);
            if (File.Exists(yamlPath)
                && serializer.LoadFromYaml(File.ReadAllText(yamlPath), clock).TryGetValue(out var staticWebhooks, out _))
            {
                foreach (var sw in staticWebhooks)
                    store.UpsertStaticWebhook(sw);
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

    private sealed class ClusterHttpClients : IDisposable
    {
        public HttpClient GossipClient { get; } = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        public HttpClient BlobClient { get; } = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        public void Dispose()
        {
            GossipClient.Dispose();
            BlobClient.Dispose();
        }
    }
}
