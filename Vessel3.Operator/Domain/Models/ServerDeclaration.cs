namespace Vessel3.Operator.Domain.Models;

public sealed record ServerVolumeDeclaration(
    string Id,
    string Path,
    string Pool = "default",
    string Capabilities = "Ingest",
    string? Size = null,
    string? StorageClassName = null);

public sealed record ServerClusterDeclaration(
    bool Enabled = false,
    long GossipIntervalMs = 1000,
    long PeerSuspectMs = 5000,
    long PeerDeadMs = 15000,
    string? SecretName = null,
    IReadOnlyList<string>? Peers = null);

public sealed record ServerDeclaration(
    ResourceIdentity Identity,
    int Replicas = 1,
    int Port = 9000,
    string StorageSize = "10Gi",
    string? StorageClassName = null,
    string? AdminSecretName = null,
    string Image = "ghcr.io/nechja/vessel3:latest",
    string ImagePullPolicy = "IfNotPresent",
    IReadOnlyList<string>? Domains = null,
    IReadOnlyList<ServerVolumeDeclaration>? Volumes = null,
    ServerClusterDeclaration? Cluster = null);
