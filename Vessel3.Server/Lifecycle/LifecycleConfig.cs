using System.Text.Json.Serialization;

namespace Vessel3.Server.Lifecycle;

internal sealed record LifecycleConfig(IReadOnlyList<LifecycleRule> Rules);

internal sealed record LifecycleRule(
    string Id,
    bool Enabled,
    string Prefix,
    int? ExpirationDays,
    bool ExpiredObjectDeleteMarker,
    int? NoncurrentDays = null);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(LifecycleConfig))]
internal sealed partial class LifecycleJsonContext : JsonSerializerContext;
