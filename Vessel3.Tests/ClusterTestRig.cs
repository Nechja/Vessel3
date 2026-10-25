using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Vessel3.Client;
using Vessel3.Server.Configuration;
using Vessel3.Server.Endpoints;
using Vessel3.Server.Hosting;
using Vessel3.Server.Pipeline;

namespace Vessel3.Tests;

internal sealed class ClusterTestRig : IAsyncDisposable
{
    private readonly string rootDir;
    private readonly ClusterChaosRouter router = new();
    private readonly Dictionary<string, ClusterTestNode> nodes = new(StringComparer.Ordinal);
    private readonly List<IDisposable> disposables = [];

    public ClusterTestRig(DateTimeOffset? initialUtc = null)
    {
        rootDir = Path.Combine(Path.GetTempPath(), $"vessel3-cluster-rig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootDir);
        Clock = new TestClock(initialUtc ?? new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    }

    public TestClock Clock { get; }

    public IReadOnlyDictionary<string, ClusterTestNode> Nodes => nodes;

    public static string LogicalEndpoint(string nodeId) => $"http://{nodeId}.cluster.local";

    public async Task<ClusterTestNode> StartNode(
        string nodeId,
        IReadOnlyList<string>? seedNodeIds = null,
        string? clusterSecret = "rig-cluster-secret",
        bool backgroundGossip = false,
        TimeSpan? suspectTimeout = null,
        TimeSpan? deadTimeout = null)
    {
        if (nodes.TryGetValue(nodeId, out var existing))
            await StopNode(existing.NodeId);

        var nodeDir = Path.Combine(rootDir, nodeId);
        var volDir = Path.Combine(nodeDir, "vol-primary");
        Directory.CreateDirectory(volDir);

        var logicalEndpoint = LogicalEndpoint(nodeId);
        IReadOnlyList<string> seedEndpoints = seedNodeIds is null
            ? []
            : [.. seedNodeIds.Select(LogicalEndpoint)];

        var volume = new StorageVolume(
            Id: $"{nodeId}-vol",
            Path: volDir,
            Pool: "default",
            Capabilities: VolumeCapabilities.Ingest,
            Status: VolumeStatus.Online,
            NodeId: nodeId);

        var clusterOptions = new ClusterMembershipOptions(
            NodeId: nodeId,
            LocalEndpoint: logicalEndpoint,
            SeedEndpoints: seedEndpoints,
            GossipInterval: backgroundGossip ? TimeSpan.FromMilliseconds(40) : TimeSpan.Zero,
            SuspectTimeout: suspectTimeout ?? TimeSpan.FromSeconds(5),
            DeadTimeout: deadTimeout ?? TimeSpan.FromSeconds(15),
            ClusterSecret: clusterSecret);

        var config = new VesselConfig(
            DataRoot: nodeDir,
            AccessKey: null,
            SecretKey: null,
            Region: "us-east-1",
            BaseDomains: [],
            GcMaxWait: TimeSpan.FromSeconds(30),
            LifecycleInterval: TimeSpan.FromHours(1),
            CompactInterval: TimeSpan.FromHours(1),
            CompactThresholdBytes: 64L * 1024 * 1024,
            SlowRequestThreshold: TimeSpan.FromSeconds(1),
            MetricsToken: null,
            MetricsAllowAnonymous: true,
            Oidc: null,
            NodeId: nodeId,
            Volumes: [volume],
            Cluster: clusterOptions);

        var nodeHttpClient = router.CreateClientForSource(nodeId);
        disposables.Add(nodeHttpClient);

        var builder = WebApplication.CreateBuilder();
        builder.ConfigureVesselHost(config);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddVessel(config);
        builder.Services.AddSingleton<TimeProvider>(Clock);
        builder.Services.AddSingleton<IGossipTransport>(new HttpGossipTransport(nodeHttpClient, clusterSecret));
        builder.Services.AddSingleton<Func<StorageVolume, IBlobReader>>(
            v => new HttpRemoteBlobReader(v.Id, v.Path, nodeHttpClient, clusterSecret));

        var app = builder.Build();
        app.UseVesselPipeline(config);
        app.MapVesselEndpoints();
        await app.StartAsync();

        var boundAddress = new Uri(app.Urls.First());
        router.RegisterNode(nodeId, logicalEndpoint, boundAddress);

        var client = new VesselClient(boundAddress.ToString());
        disposables.Add(client);

        var node = new ClusterTestNode(
            nodeId,
            logicalEndpoint,
            boundAddress,
            volDir,
            app,
            client,
            app.Services.GetRequiredService<IVolumeRegistry>(),
            app.Services.GetRequiredService<IBlobPool>(),
            app.Services.GetRequiredService<IClusterMembership>(),
            app.Services.GetRequiredService<GossipService>(),
            app.Services.GetRequiredService<IGarbageCollector>());

        nodes[nodeId] = node;
        return node;
    }

    public async Task StopNode(string nodeId)
    {
        if (!nodes.Remove(nodeId, out var node))
            return;

        router.UnregisterNode(nodeId, node.LogicalEndpoint);
        await node.App.StopAsync();
        await node.App.DisposeAsync();
    }

    public async Task StepGossip(int rounds = 1, CancellationToken ct = default)
    {
        for (var r = 0; r < rounds; r++)
        {
            foreach (var node in nodes.Values.ToArray())
            {
                await node.GossipService.RunCycle(ct);
            }
        }
    }

    public void AdvanceTime(TimeSpan delta) => Clock.Now = Clock.Now.Add(delta);

    public void Partition(string nodeA, string nodeB, bool bidirectional = true) =>
        router.AddPartition(nodeA, nodeB, bidirectional);

    public void HealPartition(string nodeA, string nodeB) =>
        router.RemovePartition(nodeA, nodeB);

    public void SetCorruptBlobsFrom(string nodeId, bool corrupt = true) =>
        router.SetCorruptBlobsFrom(nodeId, corrupt);

    public async ValueTask DisposeAsync()
    {
        foreach (var node in nodes.Values.ToArray())
        {
            router.UnregisterNode(node.NodeId, node.LogicalEndpoint);
            await node.App.StopAsync();
            await node.App.DisposeAsync();
        }
        nodes.Clear();

        foreach (var d in disposables)
        {
            d.Dispose();
        }
        disposables.Clear();

        try
        {
            if (Directory.Exists(rootDir))
                Directory.Delete(rootDir, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed record ClusterTestNode(
    string NodeId,
    string LogicalEndpoint,
    Uri BoundUri,
    string VolumePath,
    WebApplication App,
    IVesselClient Client,
    IVolumeRegistry VolumeRegistry,
    IBlobPool BlobPool,
    IClusterMembership Membership,
    GossipService GossipService,
    IGarbageCollector GarbageCollector);

internal sealed class ClusterChaosRouter
{
    private readonly ConcurrentDictionary<string, RouteTarget> targetsByHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string From, string To), byte> partitions = new();
    private readonly ConcurrentDictionary<string, bool> corruptBlobSources = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterNode(string nodeId, string logicalEndpoint, Uri boundUri)
    {
        var logicalUri = new Uri(logicalEndpoint);
        targetsByHost[logicalUri.Authority] = new RouteTarget(nodeId, boundUri);
    }

    public void UnregisterNode(string nodeId, string logicalEndpoint)
    {
        var logicalUri = new Uri(logicalEndpoint);
        targetsByHost.TryRemove(logicalUri.Authority, out _);
    }

    public void AddPartition(string fromNodeId, string toNodeId, bool bidirectional)
    {
        partitions[(fromNodeId, toNodeId)] = 1;
        if (bidirectional)
            partitions[(toNodeId, fromNodeId)] = 1;
    }

    public void RemovePartition(string nodeA, string nodeB)
    {
        partitions.TryRemove((nodeA, nodeB), out _);
        partitions.TryRemove((nodeB, nodeA), out _);
    }

    public void SetCorruptBlobsFrom(string nodeId, bool corrupt)
    {
        if (corrupt)
            corruptBlobSources[nodeId] = true;
        else
            corruptBlobSources.TryRemove(nodeId, out _);
    }

    public HttpClient CreateClientForSource(string sourceNodeId)
    {
        var socketsHandler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        };
        var chaosHandler = new NodeRoutingHandler(sourceNodeId, this, socketsHandler);
        return new HttpClient(chaosHandler)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    private sealed record RouteTarget(string NodeId, Uri BoundUri);

    private sealed class NodeRoutingHandler(
        string sourceNodeId,
        ClusterChaosRouter router,
        HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("RequestUri is null.");
            if (!router.targetsByHost.TryGetValue(uri.Authority, out var target))
                throw new HttpRequestException($"Unreachable cluster host '{uri.Authority}'.");

            if (router.partitions.ContainsKey((sourceNodeId, target.NodeId)))
                throw new HttpRequestException($"Simulated network partition between '{sourceNodeId}' and '{target.NodeId}'.");

            var rewritten = new UriBuilder(uri)
            {
                Scheme = target.BoundUri.Scheme,
                Host = target.BoundUri.Host,
                Port = target.BoundUri.Port,
            }.Uri;
            request.RequestUri = rewritten;

            var response = await base.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode
                && request.Method == HttpMethod.Get
                && uri.AbsolutePath.StartsWith("/_cluster/blobs/", StringComparison.Ordinal)
                && router.corruptBlobSources.TryGetValue(target.NodeId, out var shouldCorrupt)
                && shouldCorrupt)
            {
                var originalBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (originalBytes.Length > 0)
                {
                    originalBytes[0] ^= 0xFF;
                    response.Content = new ByteArrayContent(originalBytes);
                }
            }

            return response;
        }
    }
}
