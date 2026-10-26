using System.Text.Json.Serialization;

namespace Vessel3.Operator.Adapters.Kubernetes.Models;

public sealed record VesselServerCustomResource
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = "vessel.nechja.io/v1alpha1";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "VesselServer";

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
    public string Image { get; init; } = "ghcr.io/nechja/vessel3:latest";

    [JsonPropertyName("imagePullPolicy")]
    public string ImagePullPolicy { get; init; } = "IfNotPresent";

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
    public string Size { get; init; } = "10Gi";

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
    public string Pool { get; init; } = "default";

    [JsonPropertyName("capabilities")]
    public string Capabilities { get; init; } = "Ingest";

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
    public long GossipIntervalMs { get; init; } = 1000;

    [JsonPropertyName("peerSuspectMs")]
    public long PeerSuspectMs { get; init; } = 5000;

    [JsonPropertyName("peerDeadMs")]
    public long PeerDeadMs { get; init; } = 15000;

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
    public string Type { get; init; } = "ClusterIP";

    [JsonPropertyName("port")]
    public int Port { get; init; } = 9000;
}

public sealed record VesselServerStatus
{
    [JsonPropertyName("phase")]
    public string Phase { get; init; } = "Pending";

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
