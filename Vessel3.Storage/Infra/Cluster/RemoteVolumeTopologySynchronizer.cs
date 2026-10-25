using System.Collections.Frozen;

namespace Vessel3.Storage;

internal sealed class RemoteVolumeTopologySynchronizer(
    IVolumeRegistry volumeRegistry,
    IRemoteBlobReaderFactory readerFactory) : IClusterTopologyListener
{
    private static readonly FrozenDictionary<PeerStatus, Func<VolumeStatus, VolumeStatus>> EffectiveStatusResolver =
        new Dictionary<PeerStatus, Func<VolumeStatus, VolumeStatus>>
        {
            [PeerStatus.Alive] = static volStatus => volStatus,
            [PeerStatus.Suspect] = static _ => VolumeStatus.Faulted,
            [PeerStatus.Dead] = static _ => VolumeStatus.Faulted,
            [PeerStatus.Left] = static _ => VolumeStatus.Faulted,
        }.ToFrozenDictionary();

    public void OnPeerUpdated(PeerNodeState peer)
    {
        ArgumentNullException.ThrowIfNull(peer);

        HashSet<string> expectedIds = new(peer.Volumes.Count, StringComparer.Ordinal);
        foreach (var adv in peer.Volumes)
        {
            var qualifiedId = FormatRemoteVolumeId(peer.NodeId, adv.VolumeId);
            expectedIds.Add(qualifiedId);

            var effectiveStatus = EffectiveStatusResolver[peer.Status](adv.Status);
            var remoteVolume = new StorageVolume(
                Id: qualifiedId,
                Path: peer.Endpoint,
                Pool: adv.Pool,
                Capabilities: (adv.Capabilities & ~VolumeCapabilities.Ingest) | VolumeCapabilities.Remote | VolumeCapabilities.ReadOnly,
                Status: effectiveStatus,
                CapacityBytes: adv.CapacityBytes,
                AvailableBytes: adv.AvailableBytes,
                NodeId: peer.NodeId);

            var existing = volumeRegistry.GetVolume(qualifiedId);
            var reader = existing is not null && string.Equals(existing.Path, peer.Endpoint, StringComparison.Ordinal)
                ? volumeRegistry.GetReader(qualifiedId)
                : readerFactory.CreateReader(peer.NodeId, peer.Endpoint, adv);

            volumeRegistry.UpsertRemoteVolume(remoteVolume, reader);
        }

        RemoveOrphanedVolumes(peer.NodeId, expectedIds);
    }

    public void OnPeerRemoved(string nodeId, IReadOnlyList<AdvertisedVolume> previousVolumes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        RemoveOrphanedVolumes(nodeId, []);
    }

    public static string FormatRemoteVolumeId(ReadOnlySpan<char> nodeId, ReadOnlySpan<char> volumeId) =>
        $"{nodeId}:{volumeId}";

    private void RemoveOrphanedVolumes(string nodeId, HashSet<string> retainedIds)
    {
        foreach (var vol in volumeRegistry.Volumes)
        {
            if (!vol.IsRemote || !string.Equals(vol.NodeId, nodeId, StringComparison.Ordinal))
                continue;

            if (!retainedIds.Contains(vol.Id))
                volumeRegistry.RemoveVolume(vol.Id);
        }
    }
}
