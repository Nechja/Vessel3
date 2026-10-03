using System.Text.Json.Serialization;

namespace Vessel3.Operator.Adapters.Kubernetes.Models;

public sealed record VesselBucketCustomResource
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = "vessel.nechja.io/v1alpha1";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "VesselBucket";

    [JsonPropertyName("metadata")]
    public CustomResourceMetadata Metadata { get; init; } = new();

    [JsonPropertyName("spec")]
    public VesselBucketSpec Spec { get; init; } = new();

    [JsonPropertyName("status")]
    public VesselBucketStatus? Status { get; init; }
}

public sealed record VesselBucketSpec
{
    [JsonPropertyName("serverRef")]
    public ServerReference ServerRef { get; init; } = new();

    [JsonPropertyName("bucketName")]
    public string BucketName { get; init; } = string.Empty;

    [JsonPropertyName("versioning")]
    public string Versioning { get; init; } = "Disabled";

    [JsonPropertyName("access")]
    public string Access { get; init; } = "private";

    [JsonPropertyName("prunePolicy")]
    public string PrunePolicy { get; init; } = "Retain";

    [JsonPropertyName("website")]
    public BucketWebsiteSpec? Website { get; init; }

    [JsonPropertyName("cors")]
    public List<BucketCorsRuleSpec>? Cors { get; init; }
}

public sealed record ServerReference
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }
}

public sealed record BucketWebsiteSpec
{
    [JsonPropertyName("indexDocument")]
    public string IndexDocument { get; init; } = "index.html";

    [JsonPropertyName("errorDocument")]
    public string? ErrorDocument { get; init; }
}

public sealed record BucketCorsRuleSpec
{
    [JsonPropertyName("allowedOrigins")]
    public List<string>? AllowedOrigins { get; init; }

    [JsonPropertyName("allowedMethods")]
    public List<string>? AllowedMethods { get; init; }

    [JsonPropertyName("allowedHeaders")]
    public List<string>? AllowedHeaders { get; init; }

    [JsonPropertyName("maxAgeSeconds")]
    public int? MaxAgeSeconds { get; init; }
}

public sealed record VesselBucketStatus
{
    [JsonPropertyName("phase")]
    public string Phase { get; init; } = "Pending";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("objectCount")]
    public long ObjectCount { get; init; }

    [JsonPropertyName("observedGeneration")]
    public long? ObservedGeneration { get; init; }

    [JsonPropertyName("conditions")]
    public List<ResourceCondition>? Conditions { get; init; }
}
