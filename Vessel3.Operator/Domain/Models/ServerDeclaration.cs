using Vessel3.Primitives;

namespace Vessel3.Operator.Domain.Models;

public static class ServerDefaults
{
    public const int Port = 9000;
    public const string Image = "ghcr.io/nechja/vessel3:latest";
    public const string ImagePullPolicy = "IfNotPresent";
    public const string StorageSize = "10Gi";
    public const string VolumePool = VesselDefaults.DefaultVolumePool;
    public const string VolumeCapabilities = VesselVolumeCapabilities.Ingest;
    public const long GossipIntervalMs = 1000;
    public const long PeerSuspectMs = 5000;
    public const long PeerDeadMs = 15000;
    public const string AdminCredsSuffix = "-admin-creds";
}

public sealed record ServerVolumeDeclaration(
    string Id,
    string Path,
    string Pool = ServerDefaults.VolumePool,
    string Capabilities = ServerDefaults.VolumeCapabilities,
    string? Size = null,
    string? StorageClassName = null);

public sealed record ServerClusterDeclaration(
    bool Enabled = false,
    long GossipIntervalMs = ServerDefaults.GossipIntervalMs,
    long PeerSuspectMs = ServerDefaults.PeerSuspectMs,
    long PeerDeadMs = ServerDefaults.PeerDeadMs,
    string? SecretName = null,
    IReadOnlyList<string>? Peers = null);

public sealed record ServerDeclaration(
    ResourceIdentity Identity,
    int Replicas = 1,
    int Port = ServerDefaults.Port,
    string StorageSize = ServerDefaults.StorageSize,
    string? StorageClassName = null,
    string? AdminSecretName = null,
    string Image = ServerDefaults.Image,
    string ImagePullPolicy = ServerDefaults.ImagePullPolicy,
    IReadOnlyList<string>? Domains = null,
    IReadOnlyList<ServerVolumeDeclaration>? Volumes = null,
    ServerClusterDeclaration? Cluster = null);
