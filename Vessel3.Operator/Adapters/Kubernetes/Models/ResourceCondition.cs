using System.Text.Json.Serialization;

namespace Vessel3.Operator.Adapters.Kubernetes.Models;

public sealed record ResourceCondition
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("lastTransitionTime")]
    public string LastTransitionTime { get; init; } = DateTimeOffset.UtcNow.ToString("O");

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
