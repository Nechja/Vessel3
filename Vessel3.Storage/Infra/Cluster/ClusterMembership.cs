using System.Collections.Frozen;

namespace Vessel3.Storage;

internal sealed class ClusterMembership(
    ClusterMembershipOptions options,
    ILocalVolumeAdvertiser volumeAdvertiser,
    IEnumerable<IClusterTopologyListener>? listeners = null,
    TimeProvider? clock = null) : IClusterMembership
{
    private static readonly FrozenDictionary<PeerStatus, int> StatusPrecedence =
        new Dictionary<PeerStatus, int>
        {
            [PeerStatus.Alive] = 0,
            [PeerStatus.Suspect] = 1,
            [PeerStatus.Dead] = 2,
            [PeerStatus.Left] = 3,
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<int, Func<PeerNodeState, PeerNodeState, DateTimeOffset, bool, PeerNodeState>> VersionRules =
        new Dictionary<int, Func<PeerNodeState, PeerNodeState, DateTimeOffset, bool, PeerNodeState>>
        {
            [1] = static (_, incoming, now, _) => incoming with { LastSeenUtc = now },
            [-1] = static (current, _, _, _) => current,
            [0] = ResolveSameIncarnation,
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<PeerStatus, Func<PeerNodeState, DateTimeOffset, ClusterMembershipOptions, PeerNodeState>> SweepHandlers =
        new Dictionary<PeerStatus, Func<PeerNodeState, DateTimeOffset, ClusterMembershipOptions, PeerNodeState>>
        {
            [PeerStatus.Alive] = static (peer, now, opts) =>
                now - peer.LastSeenUtc >= opts.SuspectTimeout
                    ? peer with { Status = PeerStatus.Suspect, LastSeenUtc = now }
                    : peer,
            [PeerStatus.Suspect] = static (peer, now, opts) =>
                now - peer.LastSeenUtc >= opts.DeadTimeout
                    ? peer with { Status = PeerStatus.Dead, LastSeenUtc = now }
                    : peer,
            [PeerStatus.Dead] = static (peer, _, _) => peer,
            [PeerStatus.Left] = static (peer, _, _) => peer,
        }.ToFrozenDictionary();

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly IReadOnlyList<IClusterTopologyListener> topologyListeners = listeners is null ? [] : [.. listeners];
    private readonly Lock gate = new();

    private long localIncarnation = Math.Max(1L, (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds());
    private PeerStatus localStatus = PeerStatus.Alive;
    private IReadOnlyList<AdvertisedVolume> lastAdvertisedVolumes = volumeAdvertiser.GetAdvertisedVolumes();
    private volatile MembershipSnapshot current = MembershipSnapshot.Empty;

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
            var local = BuildLocalNodeLocked(now);
            IReadOnlyList<PeerNodeState> members = [local, .. current.PeerList];
            return new(local.NodeId, local.Endpoint, local.Incarnation, members);
        }
    }

    public GossipMessage MergeGossip(GossipMessage incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        var now = time.GetUtcNow();
        List<PeerNodeState>? topologyChanged = null;
        GossipMessage reply;

        lock (gate)
        {
            var snap = current;
            Dictionary<string, PeerNodeState>? mutated = null;

            foreach (var member in incoming.Members)
            {
                if (string.IsNullOrWhiteSpace(member.NodeId))
                    continue;

                if (string.Equals(member.NodeId, options.NodeId, StringComparison.Ordinal))
                {
                    RefuteIfNeededLocked(member);
                    continue;
                }

                var isDirectSender = string.Equals(member.NodeId, incoming.SenderNodeId, StringComparison.Ordinal);
                var existing = (mutated is not null && mutated.TryGetValue(member.NodeId, out var m))
                    ? m
                    : snap.PeersById.GetValueOrDefault(member.NodeId);

                var resolved = existing is null
                    ? member with { LastSeenUtc = now }
                    : ResolvePeerState(existing, member, now, isDirectSender);

                if (existing is not null && existing.Equals(resolved))
                    continue;

                mutated ??= new(snap.PeersById, StringComparer.Ordinal);
                mutated[member.NodeId] = resolved;

                if (existing is null || !existing.HasSameTopology(resolved))
                {
                    topologyChanged ??= [];
                    topologyChanged.Add(resolved);
                }
            }

            if (mutated is not null)
                current = CreateSnapshot(mutated);

            var local = BuildLocalNodeLocked(now);
            IReadOnlyList<PeerNodeState> members = [local, .. current.PeerList];
            reply = new(local.NodeId, local.Endpoint, local.Incarnation, members);
        }

        if (topologyChanged is not null)
            NotifyTopologyUpdated(topologyChanged);

        return reply;
    }

    public IReadOnlyList<PeerNodeState> SweepTimeouts()
    {
        var now = time.GetUtcNow();
        List<PeerNodeState>? transitioned = null;

        lock (gate)
        {
            var snap = current;
            Dictionary<string, PeerNodeState>? mutated = null;

            foreach (var peer in snap.PeerList)
            {
                var next = SweepHandlers[peer.Status](peer, now, options);
                if (ReferenceEquals(next, peer))
                    continue;

                mutated ??= new(snap.PeersById, StringComparer.Ordinal);
                mutated[peer.NodeId] = next;
                transitioned ??= [];
                transitioned.Add(next);
            }

            if (mutated is not null)
                current = CreateSnapshot(mutated);
        }

        if (transitioned is null)
            return [];

        NotifyTopologyUpdated(transitioned);
        return transitioned;
    }

    public bool MarkPeerSuspect(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (string.Equals(nodeId, options.NodeId, StringComparison.Ordinal))
            return false;

        var now = time.GetUtcNow();
        PeerNodeState? updated = null;

        lock (gate)
        {
            var snap = current;
            if (!snap.PeersById.TryGetValue(nodeId, out var existing) || existing.Status is not PeerStatus.Alive)
                return false;

            updated = existing with { Status = PeerStatus.Suspect, LastSeenUtc = now };
            Dictionary<string, PeerNodeState> mutated = new(snap.PeersById, StringComparer.Ordinal)
            {
                [nodeId] = updated
            };
            current = CreateSnapshot(mutated);
        }

        NotifyTopologyUpdated([updated]);
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

    private PeerNodeState BuildLocalNodeLocked(DateTimeOffset now)
    {
        var volumes = volumeAdvertiser.GetAdvertisedVolumes();
        if (!PeerNodeState.VolumesEqual(lastAdvertisedVolumes, volumes))
        {
            localIncarnation++;
            lastAdvertisedVolumes = volumes;
        }

        return new(
            options.NodeId,
            options.LocalEndpoint,
            localIncarnation,
            localStatus,
            now,
            lastAdvertisedVolumes);
    }

    private void RefuteIfNeededLocked(PeerNodeState peerViewOfLocal)
    {
        if (localStatus is not PeerStatus.Alive)
            return;

        if (peerViewOfLocal.Incarnation > localIncarnation
            || (peerViewOfLocal.Incarnation == localIncarnation && peerViewOfLocal.Status is not PeerStatus.Alive))
        {
            localIncarnation = Math.Max(localIncarnation, peerViewOfLocal.Incarnation) + 1;
        }
    }

    private static PeerNodeState ResolvePeerState(
        PeerNodeState current,
        PeerNodeState incoming,
        DateTimeOffset nowUtc,
        bool isDirectSender)
    {
        var cmp = Math.Sign(incoming.Incarnation.CompareTo(current.Incarnation));
        return VersionRules[cmp](current, incoming, nowUtc, isDirectSender);
    }

    private static PeerNodeState ResolveSameIncarnation(
        PeerNodeState current,
        PeerNodeState incoming,
        DateTimeOffset nowUtc,
        bool isDirectSender)
    {
        if (StatusPrecedence[incoming.Status] > StatusPrecedence[current.Status])
            return incoming with { LastSeenUtc = nowUtc };

        if (isDirectSender && current.Status is PeerStatus.Alive && incoming.Status is PeerStatus.Alive)
            return incoming with { LastSeenUtc = nowUtc };

        return current;
    }

    private static MembershipSnapshot CreateSnapshot(Dictionary<string, PeerNodeState> peers)
    {
        var frozen = peers.ToFrozenDictionary(StringComparer.Ordinal);
        IReadOnlyList<PeerNodeState> ordered = [.. peers.Values.OrderBy(p => p.NodeId, StringComparer.Ordinal)];
        return new(frozen, ordered);
    }

    private void NotifyTopologyUpdated(IReadOnlyList<PeerNodeState> updatedPeers)
    {
        if (topologyListeners.Count == 0)
            return;

        foreach (var peer in updatedPeers)
        {
            foreach (var listener in topologyListeners)
                listener.OnPeerUpdated(peer);
        }
    }

    private sealed record MembershipSnapshot(
        FrozenDictionary<string, PeerNodeState> PeersById,
        IReadOnlyList<PeerNodeState> PeerList)
    {
        public static MembershipSnapshot Empty { get; } =
            new(FrozenDictionary<string, PeerNodeState>.Empty, []);
    }
}
