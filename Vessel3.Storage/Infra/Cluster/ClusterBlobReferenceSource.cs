using System.Runtime.CompilerServices;

namespace Vessel3.Storage;

internal sealed class ClusterBlobReferenceSource(
    IClusterMembership membership,
    IBlobMirrorQueue mirrorQueue,
    IClusterReplicationTransport transport) : IBlobReferenceSource
{
    public string ProtocolName => "Cluster";

    public async IAsyncEnumerable<string> AllReferencedBlobs([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var sha in mirrorQueue.EnumeratePendingShas())
            yield return sha;

        foreach (var peer in membership.Peers)
        {
            if (peer.Status is PeerStatus.Dead or PeerStatus.Left || string.IsNullOrWhiteSpace(peer.Endpoint))
                continue;

            var res = await transport.FetchGcReferences(peer.Endpoint, ct);
            if (!res.TryGetValue(out var refs, out _))
                continue;

            foreach (var sha in refs.ReferencedShas)
                yield return sha;
        }
    }
}
