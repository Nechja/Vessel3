using System.Collections.Frozen;

namespace Vessel3.Storage;

internal static class SwimProtocol
{
    public static readonly FrozenDictionary<PeerStatus, int> StatusPrecedence =
        new Dictionary<PeerStatus, int>
        {
            [PeerStatus.Alive] = 0,
            [PeerStatus.Suspect] = 1,
            [PeerStatus.Dead] = 2,
            [PeerStatus.Left] = 3,
        }.ToFrozenDictionary();

    public static readonly FrozenDictionary<PeerStatus, Func<VolumeStatus, VolumeStatus>> EffectiveVolumeStatus =
        new Dictionary<PeerStatus, Func<VolumeStatus, VolumeStatus>>
        {
            [PeerStatus.Alive] = static volStatus => volStatus,
            [PeerStatus.Suspect] = static _ => VolumeStatus.Faulted,
            [PeerStatus.Dead] = static _ => VolumeStatus.Faulted,
            [PeerStatus.Left] = static _ => VolumeStatus.Faulted,
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<int, Func<PeerNodeState, PeerNodeState, DateTimeOffset, bool, (PeerNodeState State, bool RefreshContact)>> VersionRules =
        new Dictionary<int, Func<PeerNodeState, PeerNodeState, DateTimeOffset, bool, (PeerNodeState State, bool RefreshContact)>>
        {
            [1] = static (_, incoming, now, _) => (incoming with { LastSeenUtc = now }, true),
            [-1] = static (current, _, _, _) => (current, false),
            [0] = ResolveSameIncarnation,
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<PeerStatus, Func<TimeSpan, ClusterMembershipOptions, PeerStatus>> SweepRules =
        new Dictionary<PeerStatus, Func<TimeSpan, ClusterMembershipOptions, PeerStatus>>
        {
            [PeerStatus.Alive] = static (elapsed, opts) =>
                elapsed >= opts.SuspectTimeout ? PeerStatus.Suspect : PeerStatus.Alive,
            [PeerStatus.Suspect] = static (elapsed, opts) =>
                elapsed >= opts.DeadTimeout ? PeerStatus.Dead : PeerStatus.Suspect,
            [PeerStatus.Dead] = static (_, _) => PeerStatus.Dead,
            [PeerStatus.Left] = static (_, _) => PeerStatus.Left,
        }.ToFrozenDictionary();

    public static (PeerNodeState State, bool RefreshContact) ResolvePeerState(
        PeerNodeState current,
        PeerNodeState incoming,
        DateTimeOffset nowUtc,
        bool isDirectSender)
    {
        var cmp = Math.Sign(incoming.Incarnation.CompareTo(current.Incarnation));
        return VersionRules[cmp](current, incoming, nowUtc, isDirectSender);
    }

    public static PeerStatus EvaluateTimeoutStatus(
        PeerStatus currentStatus,
        TimeSpan elapsedSinceContact,
        ClusterMembershipOptions options) =>
        SweepRules[currentStatus](elapsedSinceContact, options);

    public static bool ShouldRefuteSelf(
        long localIncarnation,
        PeerStatus localStatus,
        PeerNodeState peerViewOfLocal) =>
        localStatus is PeerStatus.Alive
        && (peerViewOfLocal.Incarnation > localIncarnation
            || (peerViewOfLocal.Incarnation == localIncarnation && peerViewOfLocal.Status is not PeerStatus.Alive));

    public static List<(string Endpoint, string? NodeId)> SelectTargets(
        ClusterMembershipOptions options,
        IReadOnlyList<PeerNodeState> peers)
    {
        HashSet<string> seenEndpoints = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(options.NormalizedLocalEndpoint))
            seenEndpoints.Add(options.NormalizedLocalEndpoint);

        List<(string Endpoint, string? NodeId)> targets = [];

        foreach (var peer in peers)
        {
            if (peer.Status is PeerStatus.Dead or PeerStatus.Left || string.IsNullOrWhiteSpace(peer.Endpoint))
                continue;

            var normalized = peer.Endpoint.TrimEnd('/');
            if (seenEndpoints.Add(normalized))
                targets.Add((normalized, peer.NodeId));
        }

        foreach (var seed in options.NormalizedSeedEndpoints)
        {
            if (seenEndpoints.Add(seed))
                targets.Add((seed, null));
        }

        return targets;
    }

    private static (PeerNodeState State, bool RefreshContact) ResolveSameIncarnation(
        PeerNodeState current,
        PeerNodeState incoming,
        DateTimeOffset nowUtc,
        bool isDirectSender)
    {
        if (StatusPrecedence[incoming.Status] > StatusPrecedence[current.Status])
            return (incoming with { LastSeenUtc = nowUtc }, true);

        if (isDirectSender && current.Status is PeerStatus.Alive && incoming.Status is PeerStatus.Alive)
        {
            return current.HasSameState(incoming)
                ? (current, true)
                : (incoming with { LastSeenUtc = nowUtc }, true);
        }

        return (current, false);
    }
}
