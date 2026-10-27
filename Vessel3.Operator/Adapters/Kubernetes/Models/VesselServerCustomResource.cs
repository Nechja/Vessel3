using System.Text.Json.Serialization;
using Vessel3.Operator.Domain;

namespace Vessel3.Operator.Adapters.Kubernetes.Models;

public sealed record VesselServerCustomResource
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = KubernetesConstants.ApiVersion;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = KubernetesConstants.ServerKind;

    [JsonPropertyName("metadata")]
    public CustomResourceMetadata Metadata { get; init; } = new();

    [JsonPropertyName("spec")]
    public VesselServerSpec Spec { get; init; } = new();

    [JsonPropertyName("status")]
    public VesselServerStatus? Status { get; init; }
}

public sealed record VesselServerSpec
{
    [JsonPropertyName("image")]
    public string Image { get; init; } = KubernetesConstants.DefaultServerImage;

    [JsonPropertyName("imagePullPolicy")]
    public string ImagePullPolicy { get; init; } = KubernetesConstants.DefaultImagePullPolicy;

    [JsonPropertyName("replicas")]
    public int Replicas { get; init; } = 1;

    [JsonPropertyName("storage")]
    public VesselServerStorageSpec Storage { get; init; } = new();

    [JsonPropertyName("volumes")]
    public List<VesselServerVolumeSpec>? Volumes { get; init; }

    [JsonPropertyName("cluster")]
    public VesselServerClusterSpec? Cluster { get; init; }

    [JsonPropertyName("auth")]
    public VesselServerAuthSpec Auth { get; init; } = new();

    [JsonPropertyName("domains")]
    public List<string>? Domains { get; init; }

    [JsonPropertyName("service")]
    public VesselServerServiceSpec Service { get; init; } = new();
}

public sealed record VesselServerStorageSpec
{
    [JsonPropertyName("size")]
    public string Size { get; init; } = KubernetesConstants.DefaultStorageSize;

    [JsonPropertyName("storageClassName")]
    public string? StorageClassName { get; init; }
}

public sealed record VesselServerVolumeSpec
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("pool")]
    public string Pool { get; init; } = KubernetesConstants.DefaultVolumePool;

    [JsonPropertyName("capabilities")]
    public string Capabilities { get; init; } = KubernetesConstants.DefaultVolumeCapabilities;

    [JsonPropertyName("size")]
    public string? Size { get; init; }

    [JsonPropertyName("storageClassName")]
    public string? StorageClassName { get; init; }
}

public sealed record VesselServerClusterSpec
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("gossipIntervalMs")]
    public long GossipIntervalMs { get; init; } = KubernetesConstants.DefaultGossipIntervalMs;

    [JsonPropertyName("peerSuspectMs")]
    public long PeerSuspectMs { get; init; } = KubernetesConstants.DefaultPeerSuspectMs;

    [JsonPropertyName("peerDeadMs")]
    public long PeerDeadMs { get; init; } = KubernetesConstants.DefaultPeerDeadMs;

    [JsonPropertyName("secretName")]
    public string? SecretName { get; init; }

    [JsonPropertyName("peers")]
    public List<string>? Peers { get; init; }
}

public sealed record VesselServerAuthSpec
{
    [JsonPropertyName("adminSecretName")]
    public string? AdminSecretName { get; init; }

    [JsonPropertyName("autoGenerate")]
    public bool AutoGenerate { get; init; } = true;
}

public sealed record VesselServerServiceSpec
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = KubernetesConstants.ServiceTypeClusterIp;

    [JsonPropertyName("port")]
    public int Port { get; init; } = KubernetesConstants.DefaultServerPort;
}

public sealed record VesselServerStatus
{
    [JsonPropertyName("phase")]
    public string Phase { get; init; } = PhaseNames.Pending;

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }

    [JsonPropertyName("adminSecret")]
    public string? AdminSecret { get; init; }

    [JsonPropertyName("readyReplicas")]
    public int ReadyReplicas { get; init; }

    [JsonPropertyName("observedGeneration")]
    public long? ObservedGeneration { get; init; }

    [JsonPropertyName("conditions")]
    public List<ResourceCondition>? Conditions { get; init; }
}
