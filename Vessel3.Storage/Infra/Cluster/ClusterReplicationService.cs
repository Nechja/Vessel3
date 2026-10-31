using System.Collections.Concurrent;

namespace Vessel3.Storage;

internal sealed partial class ClusterReplicationService : BackgroundService, IClusterBucketSyncer, IClusterIamSyncer
{
    private readonly ClusterMembershipOptions options;
    private readonly IClusterMembership membership;
    private readonly IBucketRegistry bucketRegistry;
    private readonly IIdentityRegistry identityRegistry;
    private readonly IVolumeRegistry volumeRegistry;
    private readonly IBlobMirrorQueue mirrorQueue;
    private readonly VolumeMirrorService mirrorService;
    private readonly IClusterReplicationTransport transport;
    private readonly ILogger<ClusterReplicationService>? log;
    private readonly ConcurrentDictionary<string, long> peerIamGenerations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string PeerId, string Bucket), long> peerBucketLogCursors = new();

    public ClusterReplicationService(
        ClusterMembershipOptions options,
        IClusterMembership membership,
        IBucketRegistry bucketRegistry,
        IIdentityRegistry identityRegistry,
        IVolumeRegistry volumeRegistry,
        IBlobMirrorQueue mirrorQueue,
        VolumeMirrorService mirrorService,
        IClusterReplicationTransport transport,
        ILogger<ClusterReplicationService>? log = null)
    {
        this.options = options;
        this.membership = membership;
        this.bucketRegistry = bucketRegistry;
        this.identityRegistry = identityRegistry;
        this.volumeRegistry = volumeRegistry;
        this.mirrorQueue = mirrorQueue;
        this.mirrorService = mirrorService;
        this.transport = transport;
        this.log = log;

        bucketRegistry.RegisterClusterSyncer(this);
        identityRegistry.RegisterClusterSyncer(this);
    }

    public bool TrySyncBucketCatalog(string bucket)
    {
        var peers = membership.Peers;
        if (peers.Count == 0) return false;

        var reconciledAny = false;
        foreach (var peer in peers)
        {
            if (!IsReachablePeer(peer))
                continue;

            if (TryReconcileBucketFromPeer(peer, bucket))
                reconciledAny = true;
        }
        return reconciledAny;
    }

    private bool TryReconcileBucketFromPeer(PeerNodeState peer, string bucket)
    {
        var catalogRes = transport.FetchCatalog(peer.Endpoint).GetAwaiter().GetResult();
        if (!catalogRes.TryGetValue(out var catalog, out _))
            return false;

        var reconciled = false;
        foreach (var entry in catalog.Buckets)
        {
            if (!string.Equals(entry.Name, bucket, StringComparison.Ordinal))
                continue;

            if (bucketRegistry.ReconcileCatalogEntry(entry))
                reconciled = true;
        }
        return reconciled;
    }

    public bool TrySyncBucketEvents(string bucket)
    {
        var peers = membership.Peers;
        if (peers.Count == 0) return false;

        var localBucket = bucketRegistry.GetOpenBucket(bucket);
        if (localBucket is null) return false;

        var syncedAny = false;
        foreach (var peer in peers)
        {
            if (!IsReachablePeer(peer))
                continue;

            if (TrySyncBucketEventsFromPeer(peer, bucket, localBucket))
                syncedAny = true;
        }
        return syncedAny;
    }

    private bool TrySyncBucketEventsFromPeer(PeerNodeState peer, string bucket, Bucket localBucket)
    {
        var cursorKey = (peer.NodeId, bucket);
        var afterSeq = peerBucketLogCursors.GetValueOrDefault(cursorKey, 0L);
        var batchRes = transport.FetchBucketEvents(peer.Endpoint, bucket, afterSeq).GetAwaiter().GetResult();
        if (!batchRes.TryGetValue(out var batch, out _))
            return false;

        if (batch.RequiresSnapshot)
        {
            var snapRes = transport.OpenBucketSnapshot(peer.Endpoint, bucket).GetAwaiter().GetResult();
            if (!snapRes.TryGetValue(out var snapStream, out _))
                return false;

            using (snapStream)
            {
                localBucket.InstallSnapshot(snapStream).GetAwaiter().GetResult();
            }
            EnqueueAllBucketBlobs(localBucket, CancellationToken.None).GetAwaiter().GetResult();
            peerBucketLogCursors[cursorKey] = Math.Max(0L, batch.FirstAvailableSeq - 1);
            return true;
        }

        if (batch.Events.Count == 0)
            return false;

        var newlyApplied = localBucket.ApplyReplicatedEvents(batch.Events);
        foreach (var ev in newlyApplied)
        {
            if (ev is PutEvent put)
                EnqueuePutBlobs(put, CancellationToken.None).GetAwaiter().GetResult();
        }
        peerBucketLogCursors[cursorKey] = batch.Events[^1].Seq;
        return newlyApplied.Count > 0;
    }

    public bool TrySyncIam()
    {
        var peers = membership.Peers;
        if (peers.Count == 0) return false;

        var mergedAny = false;
        foreach (var peer in peers)
        {
            if (!IsReachablePeer(peer))
                continue;

            var iamRes = transport.FetchIamSnapshot(peer.Endpoint).GetAwaiter().GetResult();
            if (iamRes.TryGetValue(out var snap, out _) && identityRegistry.MergeSnapshot(snap))
                mergedAny = true;
        }
        return mergedAny;
    }

    public async Task<int> RunCycle(CancellationToken ct = default)
    {
        var peers = membership.Peers;
        var appliedCount = 0;

        foreach (var peer in peers)
        {
            if (!IsReachablePeer(peer))
                continue;

            appliedCount += await SyncFromPeer(peer, ct);
        }

        appliedCount += await mirrorService.DrainPending(ct);
        return appliedCount;
    }

    private async Task<int> SyncFromPeer(PeerNodeState peer, CancellationToken ct)
    {
        var catalogResult = await transport.FetchCatalog(peer.Endpoint, ct);
        if (!catalogResult.TryGetValue(out var catalog, out _))
            return 0;

        var appliedCount = 0;
        var lastPeerIam = peerIamGenerations.GetValueOrDefault(peer.NodeId, 0L);
        if (catalog.IamGeneration > lastPeerIam || catalog.IamGeneration > identityRegistry.Generation)
        {
            var iamResult = await transport.FetchIamSnapshot(peer.Endpoint, ct);
            if (iamResult.TryGetValue(out var iamSnap, out _))
            {
                if (identityRegistry.MergeSnapshot(iamSnap))
                    appliedCount++;
                peerIamGenerations[peer.NodeId] = catalog.IamGeneration;
            }
        }

        foreach (var entry in catalog.Buckets)
        {
            if (!bucketRegistry.ReconcileCatalogEntry(entry))
                continue;

            var localBucket = bucketRegistry.GetOpenBucket(entry.Name);
            if (localBucket is null)
                continue;

            appliedCount += await SyncBucketFromPeer(peer, entry, localBucket, ct);
        }

        return appliedCount;
    }

    private static bool IsReachablePeer(PeerNodeState peer) =>
        peer.Status is not (PeerStatus.Dead or PeerStatus.Left) && !string.IsNullOrWhiteSpace(peer.Endpoint);

    private async Task<int> SyncBucketFromPeer(
        PeerNodeState peer,
        BucketCatalogEntry peerEntry,
        Bucket localBucket,
        CancellationToken ct)
    {
        var cursorKey = (peer.NodeId, peerEntry.Name);
        var afterSeq = peerBucketLogCursors.GetValueOrDefault(cursorKey, 0L);
        if (afterSeq >= peerEntry.AppliedSeq && peerEntry.AppliedSeq > 0)
            return 0;

        var totalApplied = 0;
        for (var page = 0; page < 16; page++)
        {
            var batchResult = await transport.FetchBucketEvents(peer.Endpoint, peerEntry.Name, afterSeq, ct);
            if (!batchResult.TryGetValue(out var batch, out _))
                break;

            if (batch.RequiresSnapshot)
            {
                var snapResult = await transport.OpenBucketSnapshot(peer.Endpoint, peerEntry.Name, ct);
                if (!snapResult.TryGetValue(out var snapStream, out _))
                    break;

                await using (snapStream)
                {
                    await localBucket.InstallSnapshot(snapStream, peerEntry.Config, ct);
                }

                await EnqueueAllBucketBlobs(localBucket, ct);
                afterSeq = Math.Max(0L, batch.FirstAvailableSeq - 1);
                peerBucketLogCursors[cursorKey] = afterSeq;
                totalApplied++;
                continue;
            }

            if (batch.Events.Count > 0)
            {
                var newlyApplied = localBucket.ApplyReplicatedEvents(batch.Events);
                foreach (var ev in newlyApplied)
                {
                    if (ev is PutEvent put)
                        await EnqueuePutBlobs(put, ct);
                }

                totalApplied += newlyApplied.Count;
                afterSeq = batch.Events[^1].Seq;
                peerBucketLogCursors[cursorKey] = afterSeq;
            }
            else
            {
                afterSeq = batch.LatestSeq;
                peerBucketLogCursors[cursorKey] = afterSeq;
            }

            if (afterSeq >= batch.LatestSeq || batch.Events.Count == 0)
                break;
        }

        return totalApplied;
    }

    private async Task EnqueuePutBlobs(PutEvent put, CancellationToken ct)
    {
        if (volumeRegistry.MirrorTargetStorages.Count == 0)
            return;

        if (!string.IsNullOrEmpty(put.BlobSha))
            await EnqueueIfMissingLocally(put.BlobSha, ct);

        if (put.Parts is not null)
        {
            foreach (var part in put.Parts)
            {
                if (!string.IsNullOrEmpty(part.BlobSha))
                    await EnqueueIfMissingLocally(part.BlobSha, ct);
            }
        }
    }

    private async Task EnqueueAllBucketBlobs(Bucket bucket, CancellationToken ct)
    {
        if (volumeRegistry.MirrorTargetStorages.Count == 0)
            return;

        await foreach (var sha in bucket.Index.ReferencedBlobs(ct))
        {
            await EnqueueIfMissingLocally(sha, ct);
        }
    }

    private async Task EnqueueIfMissingLocally(string sha, CancellationToken ct)
    {
        foreach (var target in volumeRegistry.MirrorTargetStorages)
        {
            if (!await target.BlobExists(sha, ct))
            {
                mirrorQueue.Enqueue(sha);
                return;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsClusterEnabled || options.GossipInterval <= TimeSpan.Zero)
            return;

        using var timer = new PeriodicTimer(options.GossipInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycle(stoppingToken);
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (log is not null)
                    LogReplicationFailed(log, ex);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "cluster replication cycle failed")]
    private static partial void LogReplicationFailed(ILogger logger, Exception ex);
}
