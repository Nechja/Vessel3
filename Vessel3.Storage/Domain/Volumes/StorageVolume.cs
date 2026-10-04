namespace Vessel3.Storage;

public sealed record StorageVolume(
    string Id,
    string Path,
    string Pool = "default",
    VolumeCapabilities Capabilities = VolumeCapabilities.Ingest,
    VolumeStatus Status = VolumeStatus.Online,
    long? CapacityBytes = null)
{
    public bool IsIngest => Capabilities.HasFlag(VolumeCapabilities.Ingest);
    public bool IsOnDemand => Capabilities.HasFlag(VolumeCapabilities.OnDemand);
    public bool IsReadOnly => Capabilities.HasFlag(VolumeCapabilities.ReadOnly);
    public bool IsMirrored => Capabilities.HasFlag(VolumeCapabilities.Mirrored);
    public bool IsRemote => Capabilities.HasFlag(VolumeCapabilities.Remote);

    public string BlobsRoot => Path;
    public string TmpDir => System.IO.Path.Combine(Path, "tmp");
}
