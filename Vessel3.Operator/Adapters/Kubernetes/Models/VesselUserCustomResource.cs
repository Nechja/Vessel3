using System.Text.Json.Serialization;

namespace Vessel3.Operator.Adapters.Kubernetes.Models;

public sealed record VesselUserCustomResource
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = "vessel.nechja.io/v1alpha1";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "VesselUser";

    [JsonPropertyName("metadata")]
    public CustomResourceMetadata Metadata { get; init; } = new();

    [JsonPropertyName("spec")]
    public VesselUserSpec Spec { get; init; } = new();

    [JsonPropertyName("status")]
    public VesselUserStatus? Status { get; init; }
}

public sealed record VesselUserSpec
{
    [JsonPropertyName("serverRef")]
    public ServerReference ServerRef { get; init; } = new();

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = "Member";

    [JsonPropertyName("writeSecret")]
    public UserSecretSpec WriteSecret { get; init; } = new();
}

public sealed record UserSecretSpec
{
    [JsonPropertyName("secretName")]
    public string SecretName { get; init; } = string.Empty;

    [JsonPropertyName("secretNamespace")]
    public string? SecretNamespace { get; init; }

    [JsonPropertyName("keys")]
    public SecretKeyMapping Keys { get; init; } = new();
}

public sealed record SecretKeyMapping
{
    [JsonPropertyName("accessKey")]
    public string AccessKey { get; init; } = "AWS_ACCESS_KEY_ID";

    [JsonPropertyName("secretKey")]
    public string SecretKey { get; init; } = "AWS_SECRET_ACCESS_KEY";

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = "S3_ENDPOINT";

    [JsonPropertyName("region")]
    public string Region { get; init; } = "AWS_REGION";

    [JsonPropertyName("bucketName")]
    public string? BucketName { get; init; }
}

public sealed record VesselUserStatus
{
    [JsonPropertyName("phase")]
    public string Phase { get; init; } = "Pending";

    [JsonPropertyName("userId")]
    public string? UserId { get; init; }

    [JsonPropertyName("secretRef")]
    public string? SecretRef { get; init; }

    [JsonPropertyName("observedGeneration")]
    public long? ObservedGeneration { get; init; }

    [JsonPropertyName("conditions")]
    public List<ResourceCondition>? Conditions { get; init; }
}
