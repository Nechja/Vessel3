using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Vessel3.Tests;

public sealed class ClusterMembershipTests : IDisposable
{
    private readonly string testRoot;
    private readonly IFileSync sync = new PortableFileSync();

    public ClusterMembershipTests()
    {
        testRoot = Path.Combine(Path.GetTempPath(), $"vessel3-cluster-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(testRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Gossip_TransitivePeerExchange_ConvergesThreeNodes()
    {
        var clock = new FakeClusterClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var nodeA = CreateMembership("node-a", "http://node-a:8080", ["vol-a"], clock);
        var nodeB = CreateMembership("node-b", "http://node-b:8080", ["vol-b"], clock);
        var nodeC = CreateMembership("node-c", "http://node-c:8080", ["vol-c"], clock);

        var msgAtoB = nodeA.BuildOutboundMessage();
        var replyBtoA = nodeB.MergeGossip(msgAtoB);
        nodeA.MergeGossip(replyBtoA);

        var msgBtoC = nodeB.BuildOutboundMessage();
        var replyCtoB = nodeC.MergeGossip(msgBtoC);
        nodeB.MergeGossip(replyCtoB);

        var msgBtoA2 = nodeB.BuildOutboundMessage();
        var replyAtoB2 = nodeA.MergeGossip(msgBtoA2);
        nodeB.MergeGossip(replyAtoB2);

        Assert.Equal(2, nodeA.Peers.Count);
        Assert.Equal(2, nodeB.Peers.Count);
        Assert.Equal(2, nodeC.Peers.Count);
        Assert.NotNull(nodeA.GetPeer("node-c"));
        var peerAOnC = nodeC.GetPeer("node-a");
        Assert.NotNull(peerAOnC);
        Assert.Single(peerAOnC.Volumes);
        Assert.Equal("vol-a", peerAOnC.Volumes[0].VolumeId);
    }

    [Fact]
    public void Gossip_SelfRefutation_BumpsIncarnationWhenMarkedSuspectOrDead()
    {
        var clock = new FakeClusterClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var nodeA = CreateMembership("node-a", "http://node-a:8080", ["vol-a"], clock);
        var initialSelf = nodeA.LocalNode;

        var falseRumor = new GossipMessage(
            "node-b",
            "http://node-b:8080",
            10,
            [
                new PeerNodeState(
                    "node-a",
                    "http://node-a:8080",
                    initialSelf.Incarnation + 5,
                    PeerStatus.Suspect,
                    clock.GetUtcNow(),
                    [])
            ]);

        var response = nodeA.MergeGossip(falseRumor);
        var updatedSelf = nodeA.LocalNode;

        Assert.Equal(PeerStatus.Alive, updatedSelf.Status);
        Assert.Equal(initialSelf.Incarnation + 6, updatedSelf.Incarnation);
        Assert.Contains(response.Members, p => p.NodeId == "node-a" && p.Status == PeerStatus.Alive && p.Incarnation == initialSelf.Incarnation + 6);
    }

    [Fact]
    public void Gossip_AntiRumorLoop_AdvancesDeadNodeThroughSuspectToDead()
    {
        var clock = new FakeClusterClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var nodeA = CreateMembership("node-a", "http://node-a:8080", ["vol-a"], clock);
        var nodeB = CreateMembership("node-b", "http://node-b:8080", ["vol-b"], clock);
        var nodeC = CreateMembership("node-c", "http://node-c:8080", ["vol-c"], clock);

        nodeB.MergeGossip(nodeC.BuildOutboundMessage());
        nodeA.MergeGossip(nodeB.BuildOutboundMessage());

        for (var i = 0; i < 6; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var msg = nodeA.BuildOutboundMessage();
            var reply = nodeB.MergeGossip(msg);
            nodeA.MergeGossip(reply);
            nodeA.SweepTimeouts();
            nodeB.SweepTimeouts();
        }

        Assert.Equal(PeerStatus.Suspect, nodeA.GetPeer("node-c")!.Status);
        Assert.Equal(PeerStatus.Suspect, nodeB.GetPeer("node-c")!.Status);

        for (var i = 0; i < 16; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var msg = nodeA.BuildOutboundMessage();
            var reply = nodeB.MergeGossip(msg);
            nodeA.MergeGossip(reply);
            nodeA.SweepTimeouts();
            nodeB.SweepTimeouts();
        }

        Assert.Equal(PeerStatus.Dead, nodeA.GetPeer("node-c")!.Status);
        Assert.Equal(PeerStatus.Dead, nodeB.GetPeer("node-c")!.Status);
    }

    [Fact]
    public async Task ClusterTopology_PeerVolumeAdvertisement_EnablesRemoteBlobReadAndExists()
    {
        var clock = new FakeClusterClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var dirA = Path.Combine(testRoot, "node-a");
        var dirB = Path.Combine(testRoot, "node-b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        var volA = new StorageVolume("vol-a", dirA, "default", VolumeCapabilities.Ingest, VolumeStatus.Online, NodeId: "node-a");
        var volB = new StorageVolume("vol-b", dirB, "default", VolumeCapabilities.Ingest, VolumeStatus.Online, NodeId: "node-b");

        var regA = new VolumeRegistry([volA], sync);
        var regB = new VolumeRegistry([volB], sync);
        var poolA = new BlobPool(regA, new MemoryBlobLocationCatalog());
        var poolB = new BlobPool(regB, new MemoryBlobLocationCatalog());

        var payload = "cross-node gossip shared blob payload"u8.ToArray();
        var writeRes = await poolB.Write(new MemoryStream(payload), payload.Length, ChecksumIntent.All, CancellationToken.None);
        Assert.True(writeRes.TryGetValue(out var stored, out _));

        var readerFactoryA = new InMemoryPeerBlobReaderFactory(new Dictionary<string, IBlobPool>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://node-b:8080"] = poolB,
        });

        var syncA = new RemoteVolumeTopologySynchronizer(regA, readerFactoryA);
        var membershipA = new ClusterMembership(
            new ClusterMembershipOptions("node-a", "http://node-a:8080", ["http://node-b:8080"], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), null),
            new LocalVolumeAdvertiser(regA),
            [syncA],
            clock);
        var membershipB = new ClusterMembership(
            new ClusterMembershipOptions("node-b", "http://node-b:8080", ["http://node-a:8080"], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), null),
            new LocalVolumeAdvertiser(regB),
            [],
            clock);

        membershipA.MergeGossip(membershipB.BuildOutboundMessage());

        Assert.True(await poolA.Exists(stored.Sha));
        var openRes = await poolA.Open(stored.Sha);
        Assert.True(openRes.TryGetValue(out var stream, out _));
        using (stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(payload, ms.ToArray());
        }
    }

    [Fact]
    public async Task ClusterTopology_PeerSuspectOrDead_FaultsRemoteVolumes()
    {
        var clock = new FakeClusterClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var dirA = Path.Combine(testRoot, "node-a-fault");
        var dirB = Path.Combine(testRoot, "node-b-fault");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        var regA = new VolumeRegistry([new StorageVolume("vol-a", dirA, "default", VolumeCapabilities.Ingest, VolumeStatus.Online, NodeId: "node-a")], sync);
        var regB = new VolumeRegistry([new StorageVolume("vol-b", dirB, "default", VolumeCapabilities.Ingest, VolumeStatus.Online, NodeId: "node-b")], sync);
        var poolA = new BlobPool(regA, new MemoryBlobLocationCatalog());
        var poolB = new BlobPool(regB, new MemoryBlobLocationCatalog());

        var payload = "faulted peer blob payload"u8.ToArray();
        var writeRes = await poolB.Write(new MemoryStream(payload), payload.Length, ChecksumIntent.All, CancellationToken.None);
        Assert.True(writeRes.TryGetValue(out var stored, out _));

        var readerFactoryA = new InMemoryPeerBlobReaderFactory(new Dictionary<string, IBlobPool>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://node-b:8080"] = poolB,
        });

        var syncA = new RemoteVolumeTopologySynchronizer(regA, readerFactoryA);
        var membershipA = new ClusterMembership(
            new ClusterMembershipOptions("node-a", "http://node-a:8080", ["http://node-b:8080"], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), null),
            new LocalVolumeAdvertiser(regA),
            [syncA],
            clock);
        var membershipB = new ClusterMembership(
            new ClusterMembershipOptions("node-b", "http://node-b:8080", ["http://node-a:8080"], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), null),
            new LocalVolumeAdvertiser(regB),
            [],
            clock);

        membershipA.MergeGossip(membershipB.BuildOutboundMessage());
        Assert.True(await poolA.Exists(stored.Sha));

        membershipA.MarkPeerSuspect("node-b");
        Assert.False(await poolA.Exists(stored.Sha));
    }

    [Fact]
    public void ClusterTopology_PeerRemovesVolume_RemovesOrphanRemoteVolume()
    {
        var clock = new FakeClusterClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var dirA = Path.Combine(testRoot, "node-a-orphan");
        Directory.CreateDirectory(dirA);

        var regA = new VolumeRegistry([new StorageVolume("vol-a", dirA, "default", VolumeCapabilities.Ingest, VolumeStatus.Online, NodeId: "node-a")], sync);
        var readerFactoryA = new InMemoryPeerBlobReaderFactory(new Dictionary<string, IBlobPool>());
        var syncA = new RemoteVolumeTopologySynchronizer(regA, readerFactoryA);

        var peerWithTwoVolumes = new PeerNodeState(
            "node-b",
            "http://node-b:8080",
            10,
            PeerStatus.Alive,
            clock.GetUtcNow(),
            [
                new AdvertisedVolume("vol-b1", "default", VolumeCapabilities.Ingest, VolumeStatus.Online),
                new AdvertisedVolume("vol-b2", "default", VolumeCapabilities.Ingest, VolumeStatus.Online),
            ]);

        syncA.OnPeerUpdated(peerWithTwoVolumes);
        Assert.Equal(VolumeStatus.Online, regA.GetVolume("node-b:vol-b1")?.Status);
        Assert.Equal(VolumeStatus.Online, regA.GetVolume("node-b:vol-b2")?.Status);

        var peerWithOneVolume = peerWithTwoVolumes with
        {
            Incarnation = 11,
            Volumes = [new AdvertisedVolume("vol-b1", "default", VolumeCapabilities.Ingest, VolumeStatus.Online)],
        };

        syncA.OnPeerUpdated(peerWithOneVolume);
        Assert.Equal(VolumeStatus.Online, regA.GetVolume("node-b:vol-b1")?.Status);
        Assert.Null(regA.GetVolume("node-b:vol-b2"));
    }

    [Fact]
    public async Task ClusterTopology_ShardEnumeration_ExcludesRemoteVolumes()
    {
        var dirA = Path.Combine(testRoot, "node-a-gc");
        var dirB = Path.Combine(testRoot, "node-b-gc");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        var regA = new VolumeRegistry([new StorageVolume("vol-a", dirA, "default", VolumeCapabilities.Ingest, VolumeStatus.Online, NodeId: "node-a")], sync);
        var regB = new VolumeRegistry([new StorageVolume("vol-b", dirB, "default", VolumeCapabilities.Ingest, VolumeStatus.Online, NodeId: "node-b")], sync);
        var poolA = new BlobPool(regA, new MemoryBlobLocationCatalog());
        var poolB = new BlobPool(regB, new MemoryBlobLocationCatalog());

        var remotePayload = "remote-only blob that local gc must never enumerate"u8.ToArray();
        var writeRes = await poolB.Write(new MemoryStream(remotePayload), remotePayload.Length, ChecksumIntent.All, CancellationToken.None);
        Assert.True(writeRes.TryGetValue(out var remoteBlob, out _));

        var readerFactoryA = new InMemoryPeerBlobReaderFactory(new Dictionary<string, IBlobPool>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://node-b:8080"] = poolB,
        });

        var adv = new AdvertisedVolume("vol-b", "default", VolumeCapabilities.Ingest, VolumeStatus.Online);
        var remoteVol = new StorageVolume("node-b:vol-b", "http://node-b:8080", "default", VolumeCapabilities.Remote | VolumeCapabilities.ReadOnly, VolumeStatus.Online, NodeId: "node-b");
        regA.UpsertRemoteVolume(remoteVol, readerFactoryA.CreateReader("node-b", "http://node-b:8080", adv));

        Assert.True(await poolA.Exists(remoteBlob.Sha));
        var localShards = poolA.EnumerateShards().ToList();
        Assert.Empty(localShards);
    }

    [Fact]
    public async Task HttpClusterTransport_EndToEndGossipAndBlobFetch_VerifiesShaAndRejectsCorruption()
    {
        var payload = "verified cluster transport payload"u8.ToArray();
        var validSha = Convert.ToHexStringLower(SHA256.HashData(payload));
        var corruptPayload = "corrupted cluster transport bytes!"u8.ToArray();

        var handler = new FakeClusterHttpMessageHandler(validSha, payload, corruptPayload, "cluster-secret");
        using var httpClient = new HttpClient(handler);

        var gossipTransport = new HttpGossipTransport(httpClient, "cluster-secret");
        var outgoing = new GossipMessage("node-a", "http://node-a:8080", 1, []);
        var replyResult = await gossipTransport.ExchangeGossip("http://node-b:8080", outgoing, CancellationToken.None);
        Assert.True(replyResult.TryGetValue(out var reply, out _));
        Assert.Equal("node-b", reply.SenderNodeId);

        var adv = new AdvertisedVolume("vol-b", "default", VolumeCapabilities.Ingest, VolumeStatus.Online);
        var factory = new HttpRemoteBlobReaderFactory(httpClient, "cluster-secret");
        var reader = factory.CreateReader("node-b", "http://node-b:8080", adv);

        Assert.True(await reader.BlobExists(validSha, CancellationToken.None));
        var openRes = await reader.OpenBlob(validSha, CancellationToken.None);
        Assert.True(openRes.TryGetValue(out var stream, out _));
        using (stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(payload, ms.ToArray());
        }

        handler.ServeCorruptBytes = true;
        var corruptRes = await reader.OpenBlob(validSha, CancellationToken.None);
        Assert.False(corruptRes.TryGetValue(out _, out _));
    }

    [Fact]
    public async Task GossipCoordinator_RunCycle_ExchangesWithSeedsAndSyncsVolumes()
    {
        var clock = new FakeClusterClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var dirA = Path.Combine(testRoot, "coord-a");
        var dirB = Path.Combine(testRoot, "coord-b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        var regA = new VolumeRegistry([new StorageVolume("vol-a", dirA, "default", VolumeCapabilities.Ingest, VolumeStatus.Online, NodeId: "node-a")], sync);
        var regB = new VolumeRegistry([new StorageVolume("vol-b", dirB, "default", VolumeCapabilities.Ingest, VolumeStatus.Online, NodeId: "node-b")], sync);

        var optionsA = new ClusterMembershipOptions("node-a", "http://node-a:8080", ["http://node-b:8080"], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), null);
        var optionsB = new ClusterMembershipOptions("node-b", "http://node-b:8080", ["http://node-a:8080"], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), null);

        var syncA = new RemoteVolumeTopologySynchronizer(regA, new InMemoryPeerBlobReaderFactory(new Dictionary<string, IBlobPool>()));
        var membershipA = new ClusterMembership(optionsA, new LocalVolumeAdvertiser(regA), [syncA], clock);
        var membershipB = new ClusterMembership(optionsB, new LocalVolumeAdvertiser(regB), [], clock);

        var transport = new InMemoryGossipTransport(new Dictionary<string, IClusterMembership>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://node-b:8080"] = membershipB,
        });

        var coordinator = new GossipCoordinator(optionsA, membershipA, transport);
        var exchanged = await coordinator.RunCycle(CancellationToken.None);

        Assert.Equal(1, exchanged);
        var syncedVol = regA.GetVolume("node-b:vol-b");
        Assert.NotNull(syncedVol);
        Assert.Equal(VolumeStatus.Online, syncedVol.Status);
        Assert.True(syncedVol.IsRemote);
    }

    [Fact]
    public void ClusterConfiguration_SeedParsingAndRoutePrefix_NormalizesAndReservesClusterPrefix()
    {
        var seeds = Vessel3.Server.Configuration.VesselConfig.ParsePeerSeeds(" http://node-a:8080/ , http://node-b:8080/ ; http://NODE-A:8080 ");
        Assert.Equal(2, seeds.Length);
        Assert.Equal("http://node-a:8080", seeds[0]);
        Assert.Equal("http://node-b:8080", seeds[1]);

        Assert.True(ReservedRoutePrefixes.Matches("/_cluster"));
        Assert.True(ReservedRoutePrefixes.Matches("/_cluster/gossip"));
        Assert.True(ReservedRoutePrefixes.Matches("/_cluster/blobs/abcd"));
    }

    private static ClusterMembership CreateMembership(string nodeId, string endpointUrl, IReadOnlyList<string> volumeIds, TimeProvider clock)
    {
        var advertiser = new FakeVolumeAdvertiser(volumeIds.Select(id => new AdvertisedVolume(id, "default", VolumeCapabilities.Ingest, VolumeStatus.Online)).ToList());
        var options = new ClusterMembershipOptions(
            nodeId,
            endpointUrl,
            [],
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            null);
        return new ClusterMembership(options, advertiser, [], clock);
    }

    private sealed class FakeVolumeAdvertiser(IReadOnlyList<AdvertisedVolume> volumes) : ILocalVolumeAdvertiser
    {
        public IReadOnlyList<AdvertisedVolume> GetAdvertisedVolumes() => volumes;
    }

    private sealed class FakeClusterClock(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset currentUtc = initialUtc;
        public override DateTimeOffset GetUtcNow() => currentUtc;
        public void Advance(TimeSpan delta) => currentUtc = currentUtc.Add(delta);
    }

    private sealed class InMemoryPeerBlobReaderFactory(IReadOnlyDictionary<string, IBlobPool> poolsByEndpoint) : IRemoteBlobReaderFactory
    {
        public IBlobReader CreateReader(string nodeId, string peerEndpoint, AdvertisedVolume volume) =>
            new InMemoryPeerBlobReader(
                RemoteVolumeTopologySynchronizer.FormatRemoteVolumeId(nodeId, volume.VolumeId),
                poolsByEndpoint.GetValueOrDefault(peerEndpoint));
    }

    private sealed class InMemoryPeerBlobReader(string volumeId, IBlobPool? remotePool) : IBlobReader
    {
        public string VolumeId => volumeId;

        public Task<Result<Stream>> OpenBlob(string sha, CancellationToken ct = default) =>
            remotePool is not null
                ? remotePool.Open(sha, ct)
                : Task.FromResult<Result<Stream>>(new NotFoundError($"blob {sha}"));

        public Task<bool> BlobExists(string sha, CancellationToken ct = default) =>
            remotePool is not null ? remotePool.Exists(sha, ct) : Task.FromResult(false);
    }

    private sealed class InMemoryGossipTransport(IReadOnlyDictionary<string, IClusterMembership> peersByEndpoint) : IGossipTransport
    {
        public Task<Result<GossipMessage>> ExchangeGossip(string peerEndpoint, GossipMessage outbound, CancellationToken ct = default) =>
            Task.FromResult<Result<GossipMessage>>(
                peersByEndpoint.TryGetValue(peerEndpoint, out var peer)
                    ? peer.MergeGossip(outbound)
                    : new InvalidRequestError("Unreachable peer"));
    }

    private sealed class FakeClusterHttpMessageHandler(string validSha, byte[] validPayload, byte[] corruptPayload, string expectedSecret) : HttpMessageHandler
    {
        public bool ServeCorruptBytes { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Headers.TryGetValues(VesselHeaders.ClusterSecret, out var values) || !values.Contains(expectedSecret, StringComparer.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Post && path == "/_cluster/gossip")
            {
                var reply = new GossipMessage("node-b", "http://node-b:8080", 1, []);
                var json = JsonSerializer.Serialize(reply, GossipJsonContext.Default.GossipMessage);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                });
            }

            if (path == $"/_cluster/blobs/{validSha}")
            {
                if (request.Method == HttpMethod.Head)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

                var bytes = ServeCorruptBytes ? corruptPayload : validPayload;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
