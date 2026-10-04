using System.Text.Json.Serialization;

namespace Vessel3.Operator.Adapters.Kubernetes.Models;

public sealed record VesselWebhookCustomResource
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = "vessel.nechja.io/v1alpha1";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "VesselWebhook";

    [JsonPropertyName("metadata")]
    public CustomResourceMetadata Metadata { get; init; } = new();

    [JsonPropertyName("spec")]
    public VesselWebhookSpec Spec { get; init; } = new();

    [JsonPropertyName("status")]
    public VesselWebhookStatus? Status { get; init; }
}

public sealed record VesselWebhookSpec
{
    [JsonPropertyName("serverRef")]
    public ServerReference ServerRef { get; init; } = new();

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("secretRef")]
    public WebhookSecretSpec? SecretRef { get; init; }

    [JsonPropertyName("eventFilters")]
    public List<string>? EventFilters { get; init; }

    [JsonPropertyName("resourceFilters")]
    public List<string>? ResourceFilters { get; init; }

    [JsonPropertyName("active")]
    public bool Active { get; init; } = true;
}

public sealed record WebhookSecretSpec
{
    [JsonPropertyName("secretName")]
    public string SecretName { get; init; } = string.Empty;

    [JsonPropertyName("secretNamespace")]
    public string? SecretNamespace { get; init; }

    [JsonPropertyName("key")]
    public string Key { get; init; } = "secret";
}

public sealed record VesselWebhookStatus
{
    [JsonPropertyName("phase")]
    public string Phase { get; init; } = "Pending";

    [JsonPropertyName("webhookId")]
    public string? WebhookId { get; init; }

    [JsonPropertyName("observedGeneration")]
    public long? ObservedGeneration { get; init; }

    [JsonPropertyName("lastTriggeredAt")]
    public string? LastTriggeredAt { get; init; }

    [JsonPropertyName("lastStatusCode")]
    public int? LastStatusCode { get; init; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }

    [JsonPropertyName("conditions")]
    public List<ResourceCondition>? Conditions { get; init; }
}
