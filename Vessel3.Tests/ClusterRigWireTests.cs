using Xunit;

namespace Vessel3.Tests;

public sealed class ClusterRigWireTests
{
    [Fact]
    public async Task ThreeNodeCluster_TransitiveGossipAndRemoteBlobRead_StreamsOverRealKestrelWire()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a");
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"]);
        var nodeC = await rig.StartNode("node-c", seedNodeIds: ["node-a"]);

        await rig.StepGossip(rounds: 2);

        Assert.Equal(2, nodeA.Membership.Peers.Count);
        Assert.Equal(2, nodeB.Membership.Peers.Count);
        Assert.Equal(2, nodeC.Membership.Peers.Count);
        Assert.NotNull(nodeB.VolumeRegistry.GetVolume("node-c:node-c-vol"));
        Assert.NotNull(nodeC.VolumeRegistry.GetVolume("node-b:node-b-vol"));

        var payload = "real kestrel loopback multi-node content addressed blob"u8.ToArray();
        var writeResult = await nodeC.BlobPool.Write(
            new MemoryStream(payload),
            payload.Length,
            ChecksumIntent.All,
            CancellationToken.None);
        Assert.True(writeResult.TryGetValue(out var stored, out _));

        Assert.True(await nodeB.BlobPool.Exists(stored.Sha));
        var openResult = await nodeB.BlobPool.Open(stored.Sha);
        Assert.True(openResult.TryGetValue(out var stream, out _));
        await using (stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(payload, ms.ToArray());
        }
    }

    [Fact]
    public async Task ThreeNodeCluster_CorruptPeerStream_RejectsCorruptBytesAndFallsBackToHealthyPeer()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a");
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"]);
        var nodeC = await rig.StartNode("node-c", seedNodeIds: ["node-a"]);

        await rig.StepGossip(rounds: 2);

        var payload = "multi-replica corruption failover verification payload"u8.ToArray();
        var writeB = await nodeB.BlobPool.Write(new MemoryStream(payload), payload.Length, ChecksumIntent.All, CancellationToken.None);
        var writeC = await nodeC.BlobPool.Write(new MemoryStream(payload), payload.Length, ChecksumIntent.All, CancellationToken.None);
        Assert.True(writeB.TryGetValue(out var storedB, out _));
        Assert.True(writeC.TryGetValue(out var storedC, out _));
        Assert.Equal(storedB.Sha, storedC.Sha);

        rig.SetCorruptBlobsFrom("node-b", corrupt: true);

        var openOnA = await nodeA.BlobPool.Open(storedB.Sha);
        Assert.True(openOnA.TryGetValue(out var stream, out _));
        await using (stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(payload, ms.ToArray());
        }

        rig.SetCorruptBlobsFrom("node-c", corrupt: true);
        var bothCorruptResult = await nodeA.BlobPool.Open(storedB.Sha);
        Assert.False(bothCorruptResult.TryGetValue(out _, out _));
    }

    [Fact]
    public async Task ThreeNodeCluster_NodeCrashAndRestart_TransitionsToDeadAndRecoversOnRejoin()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a");
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"]);
        var nodeC = await rig.StartNode("node-c", seedNodeIds: ["node-a"]);

        await rig.StepGossip(rounds: 2);

        var payload = "ephemeral port restart and incarnation refutation blob"u8.ToArray();
        var writeC = await nodeC.BlobPool.Write(new MemoryStream(payload), payload.Length, ChecksumIntent.All, CancellationToken.None);
        Assert.True(writeC.TryGetValue(out var stored, out _));
        Assert.True(await nodeA.BlobPool.Exists(stored.Sha));

        await rig.StopNode("node-c");
        await rig.StepGossip(rounds: 1);

        Assert.Equal(PeerStatus.Suspect, nodeA.Membership.GetPeer("node-c")?.Status);
        Assert.Equal(VolumeStatus.Faulted, nodeA.VolumeRegistry.GetVolume("node-c:node-c-vol")?.Status);
        Assert.False(await nodeA.BlobPool.Exists(stored.Sha));

        for (var i = 0; i < 4; i++)
        {
            rig.AdvanceTime(TimeSpan.FromSeconds(5));
            await rig.StepGossip(rounds: 1);
        }

        Assert.Equal(PeerStatus.Dead, nodeA.Membership.GetPeer("node-c")?.Status);
        Assert.Equal(PeerStatus.Dead, nodeB.Membership.GetPeer("node-c")?.Status);

        rig.AdvanceTime(TimeSpan.FromSeconds(1));
        var restartedC = await rig.StartNode("node-c", seedNodeIds: ["node-a"]);
        await rig.StepGossip(rounds: 2);

        Assert.Equal(PeerStatus.Alive, nodeA.Membership.GetPeer("node-c")?.Status);
        Assert.Equal(PeerStatus.Alive, nodeB.Membership.GetPeer("node-c")?.Status);
        Assert.Equal(VolumeStatus.Online, nodeA.VolumeRegistry.GetVolume("node-c:node-c-vol")?.Status);
        Assert.NotNull(restartedC.Membership.GetPeer("node-b"));
        Assert.True(await nodeA.BlobPool.Exists(stored.Sha));
    }

    [Fact]
    public async Task ThreeNodeCluster_AsymmetricPartition_PropagatesMembershipTransitivelyViaBridgeNode()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a");
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"]);
        var nodeC = await rig.StartNode("node-c", seedNodeIds: ["node-b"]);
        var initialIncarnationC = nodeC.Membership.LocalNode.Incarnation;

        rig.Partition("node-a", "node-c", bidirectional: true);

        await rig.StepGossip(rounds: 3);

        Assert.NotNull(nodeA.Membership.GetPeer("node-c"));
        Assert.NotNull(nodeC.Membership.GetPeer("node-a"));
        Assert.Equal(PeerStatus.Suspect, nodeA.Membership.GetPeer("node-c")?.Status);
        Assert.Equal(VolumeStatus.Faulted, nodeA.VolumeRegistry.GetVolume("node-c:node-c-vol")?.Status);
        Assert.True(nodeC.Membership.LocalNode.Incarnation > initialIncarnationC);

        rig.HealPartition("node-a", "node-c");
        await rig.StepGossip(rounds: 1);

        Assert.Equal(PeerStatus.Alive, nodeA.Membership.GetPeer("node-c")?.Status);
        Assert.Equal(PeerStatus.Alive, nodeC.Membership.GetPeer("node-a")?.Status);
        Assert.Equal(VolumeStatus.Online, nodeA.VolumeRegistry.GetVolume("node-c:node-c-vol")?.Status);
    }

    [Fact]
    public async Task TwoNodeCluster_MismatchedClusterSecret_RejectsGossipAndRemoteReads()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a", clusterSecret: "alpha-secret");
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"], clusterSecret: "beta-secret");

        await rig.StepGossip(rounds: 2);

        Assert.Empty(nodeA.Membership.Peers);
        Assert.Empty(nodeB.Membership.Peers);
        Assert.Null(nodeA.VolumeRegistry.GetVolume("node-b:node-b-vol"));
    }

    [Fact]
    public async Task TwoNodeCluster_BackgroundGossipTimer_ConvergesAutomaticallyWithoutManualStepping()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a", backgroundGossip: true);
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"], backgroundGossip: true);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (nodeA.VolumeRegistry.GetVolume("node-b:node-b-vol") is { Status: VolumeStatus.Online }
                && nodeB.VolumeRegistry.GetVolume("node-a:node-a-vol") is { Status: VolumeStatus.Online })
            {
                break;
            }
            await Task.Delay(25);
        }

        Assert.Equal(VolumeStatus.Online, nodeA.VolumeRegistry.GetVolume("node-b:node-b-vol")?.Status);
        Assert.Equal(VolumeStatus.Online, nodeB.VolumeRegistry.GetVolume("node-a:node-a-vol")?.Status);
    }

    [Fact]
    public async Task TwoNodeCluster_GarbageCollectorOnPeer_NeverSweepsRemoteNodeBlobs()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a");
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"]);

        await rig.StepGossip(rounds: 2);

        var payload = "unreferenced blob on node-b that node-a gc must never touch"u8.ToArray();
        var writeB = await nodeB.BlobPool.Write(new MemoryStream(payload), payload.Length, ChecksumIntent.All, CancellationToken.None);
        Assert.True(writeB.TryGetValue(out var stored, out _));

        var blobFileOnB = Path.Combine(nodeB.VolumePath, stored.Sha[..2], stored.Sha[2..4], stored.Sha);
        File.SetLastWriteTimeUtc(blobFileOnB, DateTime.UtcNow - TimeSpan.FromHours(5));

        var gcReportOnA = await nodeA.GarbageCollector.Run(
            minBlobAge: TimeSpan.FromMinutes(1),
            minUploadAge: TimeSpan.FromMinutes(1));

        Assert.Equal(0, gcReportOnA.BlobsDeleted);
        Assert.True(File.Exists(blobFileOnB));
        Assert.True(await nodeA.BlobPool.Exists(stored.Sha));
    }
}
