using System.Text.Json.Serialization;
using Vessel3.Operator.Adapters.Kubernetes.Models;

namespace Vessel3.Operator.Adapters.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CustomResourceMetadata))]
[JsonSerializable(typeof(ResourceCondition))]
[JsonSerializable(typeof(List<ResourceCondition>))]
[JsonSerializable(typeof(VesselServerCustomResource))]
[JsonSerializable(typeof(List<VesselServerCustomResource>))]
[JsonSerializable(typeof(VesselServerSpec))]
[JsonSerializable(typeof(VesselServerStatus))]
[JsonSerializable(typeof(ServerStatusPatch))]
[JsonSerializable(typeof(VesselBucketCustomResource))]
[JsonSerializable(typeof(List<VesselBucketCustomResource>))]
[JsonSerializable(typeof(VesselBucketSpec))]
[JsonSerializable(typeof(VesselBucketStatus))]
[JsonSerializable(typeof(BucketStatusPatch))]
[JsonSerializable(typeof(VesselUserCustomResource))]
[JsonSerializable(typeof(List<VesselUserCustomResource>))]
[JsonSerializable(typeof(VesselUserSpec))]
[JsonSerializable(typeof(VesselUserStatus))]
[JsonSerializable(typeof(UserStatusPatch))]
[JsonSerializable(typeof(VesselWebhookCustomResource))]
[JsonSerializable(typeof(List<VesselWebhookCustomResource>))]
[JsonSerializable(typeof(VesselWebhookSpec))]
[JsonSerializable(typeof(WebhookSecretSpec))]
[JsonSerializable(typeof(VesselWebhookStatus))]
[JsonSerializable(typeof(WebhookStatusPatch))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class OperatorJsonContext : JsonSerializerContext;

public sealed record ServerStatusPatch([property: JsonPropertyName("status")] VesselServerStatus Status);
public sealed record BucketStatusPatch([property: JsonPropertyName("status")] VesselBucketStatus Status);
public sealed record UserStatusPatch([property: JsonPropertyName("status")] VesselUserStatus Status);
public sealed record WebhookStatusPatch([property: JsonPropertyName("status")] VesselWebhookStatus Status);
