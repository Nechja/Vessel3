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

    [Fact]
    public async Task TwoNodeCluster_GetClusterStatusViaNativeAdminApi_ReturnsVolumesAndPeers()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a");
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"]);

        await rig.StepGossip(rounds: 2);

        var statusResult = await nodeA.Client.GetClusterStatus();
        Assert.True(statusResult.TryGetValue(out var status, out _));
        Assert.Equal("node-a", status.NodeId);
        Assert.True(status.ClusterEnabled);
        Assert.Equal("node-a", status.LocalNode.NodeId);
        Assert.Equal("Alive", status.LocalNode.Status);
        Assert.Single(status.Peers);
        Assert.Equal("node-b", status.Peers[0].NodeId);
        Assert.Equal("Alive", status.Peers[0].Status);
        Assert.Equal(2, status.Volumes.Count);
        Assert.Contains(status.Volumes, v => v.Id == "node-a-vol" && !v.IsRemote && v.IsDefaultIngest && v.CapacityBytes is > 0);
        Assert.Contains(status.Volumes, v => v.Id == "node-b:node-b-vol" && v.IsRemote && !v.IsWritable && v.NodeId == "node-b");
    }

    [Fact]
    public async Task ThreeNodeActiveActive_ZeroConfigCluster_ReplicatesBucketsObjectsIamAndSurvivesTwoNodeLoss()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a", useDefaultClusterVolume: true);
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"], useDefaultClusterVolume: true);
        var nodeC = await rig.StartNode("node-c", seedNodeIds: ["node-a"], useDefaultClusterVolume: true);

        await rig.StepCluster(rounds: 2);

        Assert.True(nodeA.VolumeRegistry.DefaultIngestVolume.IsMirrored);
        Assert.True(nodeB.VolumeRegistry.DefaultIngestVolume.IsMirrored);
        Assert.True(nodeC.VolumeRegistry.DefaultIngestVolume.IsMirrored);

        Assert.True(nodeA.BucketRegistry.Create("ha-loki").TryGetValue(out _, out _));

        var userRes = nodeA.IdentityRegistry.CreateUser("grafana", UserRole.Member);
        Assert.True(userRes.TryGetValue(out var grafanaUser, out _));
        var keyRes = nodeA.IdentityRegistry.CreateAccessKey(grafanaUser.Id, "loki-reader");
        Assert.True(keyRes.TryGetValue(out var grafanaKey, out _));

        await rig.StepCluster(rounds: 2);

        Assert.True(nodeB.IdentityRegistry.AuthenticateAccessKey(grafanaKey.Id).TryGetValue(out var authOnB, out _));
        Assert.Equal("grafana", authOnB.Username);
        Assert.True(nodeC.IdentityRegistry.AuthenticateAccessKey(grafanaKey.Id).TryGetValue(out var authOnC, out _));
        Assert.Equal("grafana", authOnC.Username);

        var bytes1 = "loki-chunk-written-on-node-a"u8.ToArray();
        var bytes2 = "loki-chunk-written-on-node-b"u8.ToArray();
        var bytesSharedOld = "older-concurrent-value-on-node-a"u8.ToArray();
        var bytesSharedNew = "newer-concurrent-winner-on-node-c"u8.ToArray();

        Assert.True((await nodeA.ObjectStore.Put(new ObjectPutRequest("ha-loki", "chunks/1", new MemoryStream(bytes1), bytes1.Length))).TryGetValue(out _, out _));
        Assert.True((await nodeB.ObjectStore.Put(new ObjectPutRequest("ha-loki", "chunks/2", new MemoryStream(bytes2), bytes2.Length))).TryGetValue(out _, out _));

        Assert.True((await nodeA.ObjectStore.Put(new ObjectPutRequest("ha-loki", "chunks/shared", new MemoryStream(bytesSharedOld), bytesSharedOld.Length))).TryGetValue(out _, out _));
        await Task.Delay(15);
        Assert.True((await nodeC.ObjectStore.Put(new ObjectPutRequest("ha-loki", "chunks/shared", new MemoryStream(bytesSharedNew), bytesSharedNew.Length))).TryGetValue(out _, out _));

        await rig.StepCluster(rounds: 3);

        await rig.StopNode("node-a");
        await rig.StopNode("node-b");

        var get1OnC = await nodeC.ObjectStore.Get("ha-loki", "chunks/1");
        Assert.True(get1OnC.TryGetValue(out var obj1, out _));
        await using (obj1!.Body)
        {
            using var ms = new MemoryStream();
            await obj1.Body.CopyToAsync(ms);
            Assert.Equal(bytes1, ms.ToArray());
        }

        var get2OnC = await nodeC.ObjectStore.Get("ha-loki", "chunks/2");
        Assert.True(get2OnC.TryGetValue(out var obj2, out _));
        await using (obj2!.Body)
        {
            using var ms = new MemoryStream();
            await obj2.Body.CopyToAsync(ms);
            Assert.Equal(bytes2, ms.ToArray());
        }

        var getSharedOnC = await nodeC.ObjectStore.Get("ha-loki", "chunks/shared");
        Assert.True(getSharedOnC.TryGetValue(out var objShared, out _));
        await using (objShared!.Body)
        {
            using var ms = new MemoryStream();
            await objShared.Body.CopyToAsync(ms);
            Assert.Equal(bytesSharedNew, ms.ToArray());
        }
    }

    [Fact]
    public async Task OffsiteBackupNode_ReadOnlyMirroredOnDemand_CatchesUpAcrossCompactionAndSelfHealsPrimary()
    {
        await using var rig = new ClusterTestRig();
        var homelab = await rig.StartNode(
            "homelab",
            capabilities: VolumeCapabilities.Ingest | VolumeCapabilities.Mirrored);
        var tailscalePi = await rig.StartNode(
            "tailscale-pi",
            seedNodeIds: ["homelab"],
            capabilities: VolumeCapabilities.ReadOnly | VolumeCapabilities.Mirrored | VolumeCapabilities.OnDemand);

        await rig.StepCluster(rounds: 2);

        var directWriteAttempt = await tailscalePi.BlobPool.Write(
            new MemoryStream("client-write-to-readonly-backup"u8.ToArray()),
            31,
            ChecksumIntent.None,
            CancellationToken.None);
        Assert.False(directWriteAttempt.TryGetValue(out _, out var roErr));
        Assert.IsType<BucketIsReadOnlyError>(roErr);

        Assert.True(homelab.BucketRegistry.Create("backups").TryGetValue(out _, out _));
        var initialPayload = "initial-homelab-snapshot-0"u8.ToArray();
        Assert.True((await homelab.ObjectStore.Put(new ObjectPutRequest("backups", "snap-0", new MemoryStream(initialPayload), initialPayload.Length))).TryGetValue(out _, out _));

        await rig.StepCluster(rounds: 2);
        Assert.True(tailscalePi.BucketRegistry.GetCurrentPut("backups", "snap-0").TryGetValue(out _, out _));

        rig.Partition("homelab", "tailscale-pi", bidirectional: true);

        var offlinePayload1 = "written-while-tailscale-pi-offline-1"u8.ToArray();
        var offlinePayload2 = "written-while-tailscale-pi-offline-2"u8.ToArray();
        Assert.True((await homelab.ObjectStore.Put(new ObjectPutRequest("backups", "snap-1", new MemoryStream(offlinePayload1), offlinePayload1.Length))).TryGetValue(out _, out _));
        Assert.True((await homelab.ObjectStore.Put(new ObjectPutRequest("backups", "snap-2", new MemoryStream(offlinePayload2), offlinePayload2.Length))).TryGetValue(out var snap2Put, out _));

        var homelabBucket = homelab.BucketRegistry.GetOpenBucket("backups")!;
        Assert.True(homelabBucket.Compact().TryGetValue(out _, out _));
        Assert.True(homelabBucket.FirstAvailableLogSeq > 2);

        rig.HealPartition("homelab", "tailscale-pi");
        await rig.StepCluster(rounds: 3);

        Assert.True(tailscalePi.BucketRegistry.GetCurrentPut("backups", "snap-2").TryGetValue(out var piSnap2, out _));
        Assert.Equal(snap2Put!.Sha256, piSnap2.BlobSha);
        Assert.True(await tailscalePi.BlobPool.ExistsOnLocalVolume(snap2Put.Sha256));

        var homelabBlobFile = Path.Combine(homelab.VolumePath, snap2Put.Sha256[..2], snap2Put.Sha256[2..4], snap2Put.Sha256);
        Assert.True(File.Exists(homelabBlobFile));
        File.Delete(homelabBlobFile);
        Assert.False(await homelab.BlobPool.ExistsOnLocalVolume(snap2Put.Sha256));

        var healedRead = await homelab.ObjectStore.Get("backups", "snap-2");
        Assert.True(healedRead.TryGetValue(out var healedObj, out _));
        await using (healedObj!.Body)
        {
            using var ms = new MemoryStream();
            await healedObj.Body.CopyToAsync(ms);
            Assert.Equal(offlinePayload2, ms.ToArray());
        }

        await homelab.MirrorService.DrainPending();
        Assert.True(await homelab.BlobPool.ExistsOnLocalVolume(snap2Put.Sha256));
    }

    [Fact]
    public async Task ThreeNodeCluster_LoadBalancedMultipartUpload_StagesAcrossDifferentNodesAndCommits()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a", capabilities: VolumeCapabilities.Ingest | VolumeCapabilities.Mirrored);
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"], capabilities: VolumeCapabilities.Ingest | VolumeCapabilities.Mirrored);
        var nodeC = await rig.StartNode("node-c", seedNodeIds: ["node-a"], capabilities: VolumeCapabilities.Ingest | VolumeCapabilities.Mirrored);

        await rig.StepCluster(rounds: 2);

        Assert.True(nodeA.BucketRegistry.Create("media").TryGetValue(out _, out _));
        await rig.StepCluster(rounds: 1);

        var createSession = nodeA.ChunkStager.CreateSession("media", "video.bin", "application/octet-stream", new Dictionary<string, string>());
        Assert.True(createSession.TryGetValue(out var session, out _));

        var part1Bytes = "multipart-segment-one-staged-on-node-b-"u8.ToArray();
        var part2Bytes = "multipart-segment-two-staged-on-node-c"u8.ToArray();

        var stage1 = await nodeB.ChunkStager.StageChunk(
            session.SessionId,
            "00001",
            new MemoryStream(part1Bytes),
            part1Bytes.Length,
            DeclaredChecksums.Empty,
            CancellationToken.None);
        Assert.True(stage1.TryGetValue(out var chunk1, out _));

        var stage2 = await nodeC.ChunkStager.StageChunk(
            session.SessionId,
            "00002",
            new MemoryStream(part2Bytes),
            part2Bytes.Length,
            DeclaredChecksums.Empty,
            CancellationToken.None);
        Assert.True(stage2.TryGetValue(out var chunk2, out _));

        var listedOnA = nodeA.ChunkStager.ListChunks(session.SessionId);
        Assert.True(listedOnA.TryGetValue(out var chunks, out _));
        Assert.Equal(2, chunks.Count);

        IReadOnlyList<MultipartPart> parts =
        [
            new MultipartPart(1, chunk1.BlobSha, chunk1.Md5, chunk1.Size),
            new MultipartPart(2, chunk2.BlobSha, chunk2.Md5, chunk2.Size)
        ];

        var commitRes = await nodeA.ChunkStager.Commit(
            session.SessionId,
            parts,
            "combined-etag-2",
            ChecksumSet.Empty,
            CancellationToken.None);
        Assert.True(commitRes.TryGetValue(out _, out _));

        await rig.StepCluster(rounds: 2);

        var expectedCombined = part1Bytes.Concat(part2Bytes).ToArray();
        foreach (var node in new[] { nodeA, nodeB, nodeC })
        {
            var getRes = await node.ObjectStore.Get("media", "video.bin");
            Assert.True(getRes.TryGetValue(out var obj, out _));
            await using (obj!.Body)
            {
                using var ms = new MemoryStream();
                await obj.Body.CopyToAsync(ms);
                Assert.Equal(expectedCombined, ms.ToArray());
            }
        }
    }

    [Fact]
    public async Task TwoNodeCluster_GarbageCollector_ProtectsInFlightMultipartAndPeerReferencedBlobs()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a", capabilities: VolumeCapabilities.Ingest | VolumeCapabilities.Mirrored);
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"], capabilities: VolumeCapabilities.Ingest | VolumeCapabilities.Mirrored);

        await rig.StepCluster(rounds: 2);

        Assert.True(nodeA.BucketRegistry.Create("gc-safe").TryGetValue(out _, out _));
        var sessionRes = nodeA.ChunkStager.CreateSession("gc-safe", "inflight.bin", "application/octet-stream", new Dictionary<string, string>());
        Assert.True(sessionRes.TryGetValue(out var session, out _));

        var chunkPayload = "in-flight-chunk-protected-across-cluster-gc"u8.ToArray();
        var stagedOnA = await nodeA.ChunkStager.StageChunk(
            session.SessionId,
            "00001",
            new MemoryStream(chunkPayload),
            chunkPayload.Length,
            DeclaredChecksums.Empty,
            CancellationToken.None);
        Assert.True(stagedOnA.TryGetValue(out var chunk, out _));

        var directWriteOnB = await nodeB.BlobPool.Write(
            new MemoryStream(chunkPayload),
            chunkPayload.Length,
            ChecksumIntent.None,
            CancellationToken.None);
        Assert.True(directWriteOnB.TryGetValue(out var storedOnB, out _));
        Assert.Equal(chunk.BlobSha, storedOnB.Sha);

        var blobFileOnB = Path.Combine(nodeB.VolumePath, storedOnB.Sha[..2], storedOnB.Sha[2..4], storedOnB.Sha);
        File.SetLastWriteTimeUtc(blobFileOnB, DateTime.UtcNow - TimeSpan.FromHours(5));

        var gcReportOnB = await nodeB.GarbageCollector.Run(
            minBlobAge: TimeSpan.FromMinutes(1),
            minUploadAge: TimeSpan.FromHours(24));

        Assert.Equal(0, gcReportOnB.BlobsDeleted);
        Assert.True(File.Exists(blobFileOnB));
    }

    [Fact]
    public async Task ThreeNodeCluster_BucketLifecycle_CreateConfigUpdateDeleteAndRecreateConvergesAcrossPeers()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a", useDefaultClusterVolume: true);
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"], useDefaultClusterVolume: true);
        var nodeC = await rig.StartNode("node-c", seedNodeIds: ["node-a"], useDefaultClusterVolume: true);

        await rig.StepCluster(rounds: 2);

        Assert.True(nodeA.BucketRegistry.Create("ephemeral-bkt").TryGetValue(out _, out _));
        Assert.True(nodeA.BucketRegistry.SetVersioning("ephemeral-bkt", VersioningStatus.Enabled) is not Result.Failure);
        Assert.True(nodeA.BucketRegistry.SetAccess("ephemeral-bkt", new BucketAccess(PublicRead: true, ReadOnly: false)) is not Result.Failure);

        await rig.StepCluster(rounds: 2);

        foreach (var node in new[] { nodeA, nodeB, nodeC })
        {
            Assert.True(node.BucketRegistry.Exists("ephemeral-bkt").TryGetValue(out var exists, out _) && exists);
            Assert.True(node.BucketRegistry.GetVersioning("ephemeral-bkt").TryGetValue(out var ver, out _) && ver == VersioningStatus.Enabled);
            Assert.True(node.BucketRegistry.GetAccess("ephemeral-bkt").TryGetValue(out var acc, out _) && acc!.PublicRead);
        }

        await Task.Delay(15);
        Assert.True(nodeB.BucketRegistry.Delete("ephemeral-bkt") is not Result.Failure);
        await rig.StepCluster(rounds: 2);

        foreach (var node in new[] { nodeA, nodeB, nodeC })
        {
            Assert.True(node.BucketRegistry.Exists("ephemeral-bkt").TryGetValue(out var existsAfterDel, out _) && !existsAfterDel);
        }

        await Task.Delay(15);
        Assert.True(nodeC.BucketRegistry.Create("ephemeral-bkt").TryGetValue(out var recreated, out _) && recreated);
        await rig.StepCluster(rounds: 2);

        foreach (var node in new[] { nodeA, nodeB, nodeC })
        {
            Assert.True(node.BucketRegistry.Exists("ephemeral-bkt").TryGetValue(out var existsAfterRecreate, out _) && existsAfterRecreate);
        }
    }

    [Fact]
    public async Task ThreeNodeCluster_IamLifecycle_SuspendPromoteRevokeAndDeleteUserConvergesAcrossPeers()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a", useDefaultClusterVolume: true);
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"], useDefaultClusterVolume: true);
        var nodeC = await rig.StartNode("node-c", seedNodeIds: ["node-a"], useDefaultClusterVolume: true);

        await rig.StepCluster(rounds: 2);

        Assert.True(nodeA.IdentityRegistry.CreateUser("ops-engineer", UserRole.Member).TryGetValue(out var user, out _));
        Assert.True(nodeA.IdentityRegistry.CreateAccessKey(user!.Id, "cli-key").TryGetValue(out var key, out _));

        await rig.StepCluster(rounds: 2);
        Assert.True(nodeB.IdentityRegistry.AuthenticateAccessKey(key!.Id).TryGetValue(out var auth1, out _) && auth1!.Role == UserRole.Member);
        Assert.True(nodeC.IdentityRegistry.AuthenticateAccessKey(key.Id).TryGetValue(out var auth2, out _) && auth2!.Role == UserRole.Member);

        rig.AdvanceTime(TimeSpan.FromSeconds(2));
        Assert.True(nodeB.IdentityRegistry.UpdateUserStatus(user.Id, UserStatus.Suspended) is not Result.Failure);
        await rig.StepCluster(rounds: 2);

        Assert.False(nodeA.IdentityRegistry.AuthenticateAccessKey(key.Id).TryGetValue(out _, out var suspErrA));
        Assert.IsType<AccessDeniedError>(suspErrA);
        Assert.False(nodeC.IdentityRegistry.AuthenticateAccessKey(key.Id).TryGetValue(out _, out var suspErrC));
        Assert.IsType<AccessDeniedError>(suspErrC);

        rig.AdvanceTime(TimeSpan.FromSeconds(2));
        Assert.True(nodeC.IdentityRegistry.UpdateUserStatus(user.Id, UserStatus.Active) is not Result.Failure);
        Assert.True(nodeC.IdentityRegistry.UpdateUserRole(user.Id, UserRole.Admin) is not Result.Failure);
        await rig.StepCluster(rounds: 2);

        Assert.True(nodeA.IdentityRegistry.AuthenticateAccessKey(key.Id).TryGetValue(out var adminAuthA, out _) && adminAuthA!.IsAdmin);
        Assert.True(nodeB.IdentityRegistry.AuthenticateAccessKey(key.Id).TryGetValue(out var adminAuthB, out _) && adminAuthB!.IsAdmin);

        Assert.True(nodeA.IdentityRegistry.RevokeAccessKey(key.Id) is not Result.Failure);
        await rig.StepCluster(rounds: 2);

        Assert.False(nodeB.IdentityRegistry.AuthenticateAccessKey(key.Id).TryGetValue(out _, out _));
        Assert.False(nodeC.IdentityRegistry.AuthenticateAccessKey(key.Id).TryGetValue(out _, out _));

        rig.AdvanceTime(TimeSpan.FromSeconds(2));
        Assert.True(nodeB.IdentityRegistry.DeleteUser(user.Id) is not Result.Failure);
        await rig.StepCluster(rounds: 2);

        Assert.False(nodeA.IdentityRegistry.GetUser(user.Id).TryGetValue(out _, out _));
        Assert.False(nodeC.IdentityRegistry.GetUser(user.Id).TryGetValue(out _, out _));
    }

    [Fact]
    public async Task ThreeNodeCluster_SplitBrainPartitionAndHeal_ConvergesAllKeysDeletesAndListOrderDeterministically()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a", useDefaultClusterVolume: true);
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"], useDefaultClusterVolume: true);
        var nodeC = await rig.StartNode("node-c", seedNodeIds: ["node-a"], useDefaultClusterVolume: true);

        await rig.StepCluster(rounds: 2);

        Assert.True(nodeA.BucketRegistry.Create("split-brain").TryGetValue(out _, out _));
        var preBytes = "pre-partition-object"u8.ToArray();
        Assert.True((await nodeA.ObjectStore.Put(new ObjectPutRequest("split-brain", "to-be-deleted", new MemoryStream(preBytes), preBytes.Length))).TryGetValue(out _, out _));
        await rig.StepCluster(rounds: 2);

        rig.Partition("node-a", "node-b", bidirectional: true);
        rig.Partition("node-a", "node-c", bidirectional: true);

        var conflictFromA = "conflict-from-isolated-node-a"u8.ToArray();
        var onlyOnA = "written-only-on-isolated-node-a"u8.ToArray();
        Assert.True((await nodeA.ObjectStore.Put(new ObjectPutRequest("split-brain", "shared-key", new MemoryStream(conflictFromA), conflictFromA.Length))).TryGetValue(out _, out _));
        Assert.True((await nodeA.ObjectStore.Put(new ObjectPutRequest("split-brain", "only-a", new MemoryStream(onlyOnA), onlyOnA.Length))).TryGetValue(out _, out _));

        await Task.Delay(20);
        var conflictFromB = "conflict-winner-from-majority-node-b"u8.ToArray();
        var onlyOnC = "written-on-node-c-during-partition"u8.ToArray();
        Assert.True((await nodeB.ObjectStore.Put(new ObjectPutRequest("split-brain", "shared-key", new MemoryStream(conflictFromB), conflictFromB.Length))).TryGetValue(out _, out _));
        Assert.True(nodeB.ObjectStore.Delete("split-brain", "to-be-deleted").TryGetValue(out _, out _));
        Assert.True((await nodeC.ObjectStore.Put(new ObjectPutRequest("split-brain", "only-c", new MemoryStream(onlyOnC), onlyOnC.Length))).TryGetValue(out _, out _));

        rig.HealPartition("node-a", "node-b");
        rig.HealPartition("node-a", "node-c");
        await rig.StepCluster(rounds: 4);

        foreach (var node in new[] { nodeA, nodeB, nodeC })
        {
            Assert.False(node.ObjectStore.Stat("split-brain", "to-be-deleted").TryGetValue(out _, out _));

            var listRes = node.BucketRegistry.ListCurrent("split-brain", prefix: null, from: null, limit: 100);
            Assert.True(listRes.TryGetValue(out var page, out _));
            Assert.Equal(["only-a", "only-c", "shared-key"], page!.Entries.Select(e => e.Key).ToArray());

            var sharedRes = await node.ObjectStore.Get("split-brain", "shared-key");
            Assert.True(sharedRes.TryGetValue(out var sharedObj, out _));
            await using (sharedObj!.Body)
            {
                using var ms = new MemoryStream();
                await sharedObj.Body.CopyToAsync(ms);
                Assert.Equal(conflictFromB, ms.ToArray());
            }
        }
    }

    [Fact]
    public async Task ThreeNodeCluster_VersionedBucketTagsAndHardDelete_ReplicateFaithfullyAcrossAllPeers()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a", useDefaultClusterVolume: true);
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"], useDefaultClusterVolume: true);
        var nodeC = await rig.StartNode("node-c", seedNodeIds: ["node-a"], useDefaultClusterVolume: true);

        await rig.StepCluster(rounds: 2);

        Assert.True(nodeA.BucketRegistry.Create("versioned-ha").TryGetValue(out _, out _));
        Assert.True(nodeA.BucketRegistry.SetVersioning("versioned-ha", VersioningStatus.Enabled) is not Result.Failure);
        await rig.StepCluster(rounds: 2);

        var v1Bytes = "version-1-on-node-a"u8.ToArray();
        var v2Bytes = "version-2-on-node-b"u8.ToArray();

        Assert.True((await nodeA.ObjectStore.Put(new ObjectPutRequest("versioned-ha", "doc.txt", new MemoryStream(v1Bytes), v1Bytes.Length))).TryGetValue(out var put1, out _));
        await rig.StepCluster(rounds: 2);

        await Task.Delay(15);
        Assert.True((await nodeB.ObjectStore.Put(new ObjectPutRequest("versioned-ha", "doc.txt", new MemoryStream(v2Bytes), v2Bytes.Length))).TryGetValue(out var put2, out _));
        Assert.True(nodeB.ObjectStore.PutTagging("versioned-ha", "doc.txt", put2!.VersionId, new Dictionary<string, string> { ["env"] = "prod", ["tier"] = "ha" }).TryGetValue(out _, out _));

        await rig.StepCluster(rounds: 2);

        Assert.True(nodeC.ObjectStore.GetTagging("versioned-ha", "doc.txt", put2.VersionId).TryGetValue(out var tagsOnC, out _));
        Assert.Equal("prod", tagsOnC!["env"]);
        Assert.Equal("ha", tagsOnC["tier"]);

        Assert.True(nodeC.ObjectStore.DeleteVersion("versioned-ha", "doc.txt", put1!.VersionId).TryGetValue(out _, out _));
        await rig.StepCluster(rounds: 2);

        foreach (var node in new[] { nodeA, nodeB, nodeC })
        {
            var deletedGet = await node.ObjectStore.Get("versioned-ha", "doc.txt", put1.VersionId);
            Assert.False(deletedGet.TryGetValue(out _, out _));
            var v2Get = await node.ObjectStore.Get("versioned-ha", "doc.txt", put2.VersionId);
            Assert.True(v2Get.TryGetValue(out var v2Obj, out _));
            await using (v2Obj!.Body)
            {
                using var ms = new MemoryStream();
                await v2Obj.Body.CopyToAsync(ms);
                Assert.Equal(v2Bytes, ms.ToArray());
            }
        }
    }

    [Fact]
    public async Task ThreeNodeCluster_InstantRoundRobinWithoutReplicationTick_ServesIamBucketPutAndGetAcrossNodesOnDemand()
    {
        await using var rig = new ClusterTestRig();
        var nodeA = await rig.StartNode("node-a", useDefaultClusterVolume: true);
        var nodeB = await rig.StartNode("node-b", seedNodeIds: ["node-a"], useDefaultClusterVolume: true);
        var nodeC = await rig.StartNode("node-c", seedNodeIds: ["node-a"], useDefaultClusterVolume: true);

        await rig.StepGossip(rounds: 2);

        Assert.True(nodeA.IdentityRegistry.CreateUser("instant-user", UserRole.Member).TryGetValue(out var user, out _));
        Assert.True(nodeA.IdentityRegistry.CreateAccessKey(user!.Id, "instant-key").TryGetValue(out var key, out _));

        Assert.True(nodeB.IdentityRegistry.AuthenticateAccessKey(key!.Id).TryGetValue(out var authOnB, out _));
        Assert.Equal("instant-user", authOnB!.Username);

        Assert.True(nodeA.BucketRegistry.Create("instant-rr").TryGetValue(out _, out _));

        var payload = "written-on-b-immediately-after-bucket-create-on-a"u8.ToArray();
        var putOnB = await nodeB.ObjectStore.Put(new ObjectPutRequest("instant-rr", "item-1", new MemoryStream(payload), payload.Length));
        Assert.True(putOnB.TryGetValue(out _, out _));

        var getOnC = await nodeC.ObjectStore.Get("instant-rr", "item-1");
        Assert.True(getOnC.TryGetValue(out var objOnC, out _));
        await using (objOnC!.Body)
        {
            using var ms = new MemoryStream();
            await objOnC.Body.CopyToAsync(ms);
            Assert.Equal(payload, ms.ToArray());
        }
    }
}
