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
