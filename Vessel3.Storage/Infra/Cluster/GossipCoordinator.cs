namespace Vessel3.Storage;

internal sealed class GossipCoordinator(
    ClusterMembershipOptions options,
    IClusterMembership membership,
    IGossipTransport transport)
{
    public async Task<int> RunCycle(CancellationToken ct = default)
    {
        var targets = CollectTargets();
        var exchanged = 0;

        foreach (var (endpoint, knownNodeId) in targets)
        {
            var outbound = membership.BuildOutboundMessage();
            var result = await transport.ExchangeGossip(endpoint, outbound, ct);
            if (result.TryGetValue(out var reply, out _))
            {
                membership.MergeGossip(reply);
                exchanged++;
                continue;
            }

            if (knownNodeId is not null)
                membership.MarkPeerSuspect(knownNodeId);
        }

        membership.SweepTimeouts();
        return exchanged;
    }

    private List<(string Endpoint, string? NodeId)> CollectTargets()
    {
        var localEndpoint = options.LocalEndpoint;
        HashSet<string> seenEndpoints = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(localEndpoint))
            seenEndpoints.Add(localEndpoint.TrimEnd('/'));

        List<(string Endpoint, string? NodeId)> targets = [];

        foreach (var peer in membership.Peers)
        {
            if (peer.Status is PeerStatus.Dead or PeerStatus.Left || string.IsNullOrWhiteSpace(peer.Endpoint))
                continue;

            var normalized = peer.Endpoint.TrimEnd('/');
            if (seenEndpoints.Add(normalized))
                targets.Add((normalized, peer.NodeId));
        }

        foreach (var seed in options.SeedEndpoints)
        {
            if (string.IsNullOrWhiteSpace(seed))
                continue;

            var normalized = seed.TrimEnd('/');
            if (seenEndpoints.Add(normalized))
                targets.Add((normalized, null));
        }

        return targets;
    }
}
