using System.Collections.Frozen;

namespace Vessel3.Storage;

internal sealed class ClusterMembership : IClusterMembership
{
    private readonly ClusterMembershipOptions options;
    private readonly IVolumeRegistry? volumeRegistry;
    private readonly Func<IReadOnlyList<AdvertisedVolume>> advertisedVolumesProvider;
    private readonly TimeProvider time;
    private readonly Lock gate = new();

    private long localIncarnation;
    private long topologySequence;
    private PeerStatus localStatus = PeerStatus.Alive;
    private IReadOnlyList<AdvertisedVolume> lastAdvertisedVolumes;
    private volatile MembershipSnapshot current = MembershipSnapshot.Empty;

    public ClusterMembership(
        ClusterMembershipOptions options,
        IVolumeRegistry? volumeRegistry = null,
        TimeProvider? clock = null,
        Func<IReadOnlyList<AdvertisedVolume>>? localVolumes = null)
    {
        this.options = options;
        this.volumeRegistry = volumeRegistry;
        time = clock ?? TimeProvider.System;
        advertisedVolumesProvider = localVolumes ?? (() => this.volumeRegistry?.LocalAdvertisedVolumes ?? []);
        localIncarnation = Math.Max(1L, time.GetUtcNow().ToUnixTimeMilliseconds());
        lastAdvertisedVolumes = advertisedVolumesProvider();
    }

    public PeerNodeState LocalNode
    {
        get
        {
            lock (gate)
            {
                return BuildLocalNodeLocked(time.GetUtcNow());
            }
        }
    }

    public IReadOnlyList<PeerNodeState> Peers => current.PeerList;

    public PeerNodeState? GetPeer(string nodeId) =>
        current.PeersById.GetValueOrDefault(nodeId);

    public GossipMessage BuildOutboundMessage()
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            return BuildOutboundMessageLocked(now);
        }
    }

    public GossipMessage MergeGossip(GossipMessage incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        var now = time.GetUtcNow();
        var nowTicks = now.UtcTicks;
        var topologyChanged = false;
        PeerTopologySnapshot? topologyToSync = null;
        GossipMessage reply;

        lock (gate)
        {
            var snap = current;
            Dictionary<string, PeerNodeState>? mutatedPeers = null;
            Dictionary<string, long>? mutatedContacts = null;

            foreach (var member in incoming.Members)
            {
                if (string.IsNullOrWhiteSpace(member.NodeId))
                    continue;

                if (string.Equals(member.NodeId, options.NodeId, StringComparison.Ordinal))
                {
                    if (SwimProtocol.ShouldRefuteSelf(localIncarnation, localStatus, member))
                        localIncarnation = Math.Max(localIncarnation, member.Incarnation) + 1;
                    continue;
                }

                var isDirectSender = string.Equals(member.NodeId, incoming.SenderNodeId, StringComparison.Ordinal);
                var existing = mutatedPeers is not null && mutatedPeers.TryGetValue(member.NodeId, out var m)
                    ? m
                    : snap.PeersById.GetValueOrDefault(member.NodeId);

                if (existing is null)
                {
                    var added = member with { LastSeenUtc = now };
                    mutatedPeers ??= new(snap.PeersById, StringComparer.Ordinal);
                    mutatedPeers[member.NodeId] = added;
                    mutatedContacts ??= CloneContacts(snap);
                    mutatedContacts[member.NodeId] = nowTicks;
                    topologyChanged = true;
                    continue;
                }

                var (resolved, refreshContact) = SwimProtocol.ResolvePeerState(existing, member, now, isDirectSender);

                if (!ReferenceEquals(existing, resolved) && !existing.HasSameState(resolved))
                {
                    mutatedPeers ??= new(snap.PeersById, StringComparer.Ordinal);
                    mutatedPeers[member.NodeId] = resolved;
                    mutatedContacts ??= CloneContacts(snap);
                    mutatedContacts[member.NodeId] = nowTicks;
                    if (!existing.HasSameTopology(resolved))
                        topologyChanged = true;
                }
                else if (refreshContact && snap.PeerIndexById.TryGetValue(member.NodeId, out var idx))
                {
                    Volatile.Write(ref snap.ContactTicks[idx], nowTicks);
                }
            }

            if (mutatedPeers is not null)
            {
                var nextVersion = topologyChanged ? ++topologySequence : snap.TopologyVersion;
                current = CreateSnapshot(mutatedPeers, mutatedContacts!, nextVersion);
                if (topologyChanged)
                    topologyToSync = new(current.TopologyVersion, current.PeerList);
            }

            reply = BuildOutboundMessageLocked(now);
        }

        if (topologyToSync is { } syncSnap)
            volumeRegistry?.SyncPeerTopology(syncSnap);

        return reply;
    }

    public IReadOnlyList<PeerNodeState> SweepTimeouts()
    {
        var now = time.GetUtcNow();
        var nowTicks = now.UtcTicks;
        List<PeerNodeState>? transitioned = null;
        PeerTopologySnapshot? topologyToSync = null;

        lock (gate)
        {
            var snap = current;
            Dictionary<string, PeerNodeState>? mutatedPeers = null;
            Dictionary<string, long>? mutatedContacts = null;

            for (var i = 0; i < snap.PeerList.Count; i++)
            {
                var peer = snap.PeerList[i];
                var contactTicks = Volatile.Read(ref snap.ContactTicks[i]);
                var elapsed = TimeSpan.FromTicks(nowTicks - contactTicks);
                var nextStatus = SwimProtocol.EvaluateTimeoutStatus(peer.Status, elapsed, options);
                if (nextStatus == peer.Status)
                    continue;

                var nextPeer = peer with { Status = nextStatus, LastSeenUtc = now };
                mutatedPeers ??= new(snap.PeersById, StringComparer.Ordinal);
                mutatedPeers[peer.NodeId] = nextPeer;
                mutatedContacts ??= CloneContacts(snap);
                mutatedContacts[peer.NodeId] = nowTicks;
                transitioned ??= [];
                transitioned.Add(nextPeer);
            }

            if (mutatedPeers is not null)
            {
                current = CreateSnapshot(mutatedPeers, mutatedContacts!, ++topologySequence);
                topologyToSync = new(current.TopologyVersion, current.PeerList);
            }
        }

        if (topologyToSync is { } syncSnap)
            volumeRegistry?.SyncPeerTopology(syncSnap);

        return transitioned ?? [];
    }

    public bool MarkPeerSuspect(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (string.Equals(nodeId, options.NodeId, StringComparison.Ordinal))
            return false;

        var now = time.GetUtcNow();
        var nowTicks = now.UtcTicks;
        PeerTopologySnapshot topologyToSync;

        lock (gate)
        {
            var snap = current;
            if (!snap.PeersById.TryGetValue(nodeId, out var existing) || existing.Status is not PeerStatus.Alive)
                return false;

            var updated = existing with { Status = PeerStatus.Suspect, LastSeenUtc = now };
            Dictionary<string, PeerNodeState> mutatedPeers = new(snap.PeersById, StringComparer.Ordinal)
            {
                [nodeId] = updated
            };
            var mutatedContacts = CloneContacts(snap);
            mutatedContacts[nodeId] = nowTicks;

            current = CreateSnapshot(mutatedPeers, mutatedContacts, ++topologySequence);
            topologyToSync = new(current.TopologyVersion, current.PeerList);
        }

        volumeRegistry?.SyncPeerTopology(topologyToSync);
        return true;
    }

    public void Leave()
    {
        lock (gate)
        {
            localIncarnation++;
            localStatus = PeerStatus.Left;
        }
    }

    private GossipMessage BuildOutboundMessageLocked(DateTimeOffset now)
    {
        var local = BuildLocalNodeLocked(now);
        IReadOnlyList<PeerNodeState> members = [local, .. current.PeerList];
        return new(local.NodeId, local.Endpoint, local.Incarnation, members);
    }

    private PeerNodeState BuildLocalNodeLocked(DateTimeOffset now)
    {
        var volumes = advertisedVolumesProvider();
        if (!ReferenceEquals(lastAdvertisedVolumes, volumes) && !PeerNodeState.VolumesEqual(lastAdvertisedVolumes, volumes))
        {
            localIncarnation++;
            lastAdvertisedVolumes = volumes;
        }
        else
        {
            lastAdvertisedVolumes = volumes;
        }

        return new(
            options.NodeId,
            options.NormalizedLocalEndpoint,
            localIncarnation,
            localStatus,
            now,
            lastAdvertisedVolumes);
    }

    private static Dictionary<string, long> CloneContacts(MembershipSnapshot snap)
    {
        Dictionary<string, long> map = new(snap.PeerList.Count, StringComparer.Ordinal);
        for (var i = 0; i < snap.PeerList.Count; i++)
            map[snap.PeerList[i].NodeId] = Volatile.Read(ref snap.ContactTicks[i]);
        return map;
    }

    private static MembershipSnapshot CreateSnapshot(
        Dictionary<string, PeerNodeState> peers,
        Dictionary<string, long> contactsById,
        long topologyVersion)
    {
        var frozenPeers = peers.ToFrozenDictionary(StringComparer.Ordinal);
        IReadOnlyList<PeerNodeState> ordered = [.. peers.Values.OrderBy(static p => p.NodeId, StringComparer.Ordinal)];
        Dictionary<string, int> indexMap = new(ordered.Count, StringComparer.Ordinal);
        var contactTicks = new long[ordered.Count];

        for (var i = 0; i < ordered.Count; i++)
        {
            var nodeId = ordered[i].NodeId;
            indexMap[nodeId] = i;
            contactTicks[i] = contactsById.TryGetValue(nodeId, out var ticks)
                ? ticks
                : ordered[i].LastSeenUtc.UtcTicks;
        }

        return new(
            frozenPeers,
            indexMap.ToFrozenDictionary(StringComparer.Ordinal),
            ordered,
            contactTicks,
            topologyVersion);
    }

    private sealed record MembershipSnapshot(
        FrozenDictionary<string, PeerNodeState> PeersById,
        FrozenDictionary<string, int> PeerIndexById,
        IReadOnlyList<PeerNodeState> PeerList,
        long[] ContactTicks,
        long TopologyVersion)
    {
        public static MembershipSnapshot Empty { get; } =
            new(
                FrozenDictionary<string, PeerNodeState>.Empty,
                FrozenDictionary<string, int>.Empty,
                [],
                [],
                0L);
    }
}
