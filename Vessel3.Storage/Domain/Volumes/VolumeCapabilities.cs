namespace Vessel3.Storage;

[Flags]
public enum VolumeCapabilities
{
    None = 0,
    Ingest = 1 << 0,
    OnDemand = 1 << 1,
    ReadOnly = 1 << 2,
    Mirrored = 1 << 3,
    Remote = 1 << 4
}
