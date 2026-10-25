namespace Vessel3.Storage;

public sealed record StorageVolume(
    string Id,
    string Path,
    string Pool = "default",
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

    public string BlobsRoot => Path;
    public string TmpDir { get; } = System.IO.Path.Combine(Path, "tmp");

    public static StorageVolume CreateDefault(string dataRoot) =>
        new("default", System.IO.Path.Combine(dataRoot, "blobs"), "default", VolumeCapabilities.Ingest);
}
