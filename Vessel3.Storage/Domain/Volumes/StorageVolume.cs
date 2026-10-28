namespace Vessel3.Storage;

public sealed record StorageVolume(
    string Id,
    string Path,
    string Pool = VesselDefaults.DefaultVolumePool,
    VolumeCapabilities Capabilities = VolumeCapabilities.Ingest,
    VolumeStatus Status = VolumeStatus.Online,
    long? CapacityBytes = null,
    long? AvailableBytes = null,
    string? NodeId = null)
{
    public bool IsIngest => (Capabilities & VolumeCapabilities.Ingest) != 0;
    public bool IsOnDemand => (Capabilities & VolumeCapabilities.OnDemand) != 0;
    public bool IsReadOnly => (Capabilities & VolumeCapabilities.ReadOnly) != 0;
    public bool IsMirrored => (Capabilities & VolumeCapabilities.Mirrored) != 0;
    public bool IsRemote => (Capabilities & VolumeCapabilities.Remote) != 0;
    public bool IsOnline => Status is VolumeStatus.Online;
    public bool IsReadable => Status is not VolumeStatus.Faulted;
    public bool IsWritable => !IsReadOnly && !IsRemote && IsOnline;
    public bool IsLocalReplicaWritable => !IsRemote && IsOnline && (!IsReadOnly || IsMirrored);
    public bool IsLocalMirrorTarget => !IsRemote && IsOnline && IsMirrored;

    public string BlobsRoot => Path;
    public string TmpDir { get; } = (Capabilities & VolumeCapabilities.Remote) != 0
        ? string.Empty
        : System.IO.Path.Combine(Path, VesselDefaults.TmpSubdirectory);

    public AdvertisedVolume ToAdvertised() =>
        new(Id, Pool, Capabilities, Status, CapacityBytes, AvailableBytes);

    public static StorageVolume CreateDefault(string dataRoot, string? nodeId = null, bool clusterEnabled = false) =>
        new(
            VesselDefaults.DefaultVolumeId,
            System.IO.Path.Combine(dataRoot, VesselDefaults.BlobsSubdirectory),
            VesselDefaults.DefaultVolumePool,
            clusterEnabled ? VolumeCapabilities.Ingest | VolumeCapabilities.Mirrored : VolumeCapabilities.Ingest,
            NodeId: nodeId);

    public static StorageVolume CreateRemote(
        PeerNodeState peer,
        AdvertisedVolume advertised,
        VolumeStatus effectiveStatus) =>
        new(
            Id: FormatRemoteId(peer.NodeId, advertised.VolumeId),
            Path: peer.Endpoint,
            Pool: advertised.Pool,
            Capabilities: NormalizeRemoteCapabilities(advertised.Capabilities),
            Status: effectiveStatus,
            CapacityBytes: advertised.CapacityBytes,
            AvailableBytes: advertised.AvailableBytes,
            NodeId: peer.NodeId);

    public static string FormatRemoteId(ReadOnlySpan<char> nodeId, ReadOnlySpan<char> volumeId) =>
        $"{nodeId}:{volumeId}";

    public static VolumeCapabilities NormalizeRemoteCapabilities(VolumeCapabilities capabilities) =>
        (capabilities & ~VolumeCapabilities.Ingest) | VolumeCapabilities.Remote | VolumeCapabilities.ReadOnly;
}
