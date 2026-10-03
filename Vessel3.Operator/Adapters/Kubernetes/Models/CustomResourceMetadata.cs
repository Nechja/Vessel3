using System.Text.Json.Serialization;

namespace Vessel3.Operator.Adapters.Kubernetes.Models;

public sealed record CustomResourceMetadata
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("namespace")]
    public string Namespace { get; init; } = string.Empty;

    [JsonPropertyName("generation")]
    public long? Generation { get; init; }

    [JsonPropertyName("resourceVersion")]
    public string? ResourceVersion { get; init; }

    [JsonPropertyName("uid")]
    public string? Uid { get; init; }

    [JsonPropertyName("deletionTimestamp")]
    public string? DeletionTimestamp { get; init; }

    [JsonPropertyName("finalizers")]
    public List<string>? Finalizers { get; init; }
}
