using System.Text.Json.Serialization;

namespace Vessel3.Server;

internal sealed record LifecycleConfig(IReadOnlyList<LifecycleRule> Rules);

internal sealed record LifecycleRule(
    string Id,
    bool Enabled,
    string Prefix,
    int? ExpirationDays,
    bool ExpiredObjectDeleteMarker);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(LifecycleConfig))]
internal sealed partial class LifecycleJsonContext : JsonSerializerContext;
