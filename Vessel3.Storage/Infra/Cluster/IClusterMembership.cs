namespace Vessel3.Storage;

internal interface IClusterMembership
{
    PeerNodeState LocalNode { get; }
    IReadOnlyList<PeerNodeState> Peers { get; }
    PeerNodeState? GetPeer(string nodeId);
    GossipMessage BuildOutboundMessage();
    GossipMessage MergeGossip(GossipMessage incoming);
    IReadOnlyList<PeerNodeState> SweepTimeouts();
    bool MarkPeerSuspect(string nodeId);
    void Leave();
}

internal interface IGossipTransport
{
    Task<Result<GossipMessage>> ExchangeGossip(
        string peerEndpoint,
        GossipMessage outbound,
        CancellationToken ct = default);
}
