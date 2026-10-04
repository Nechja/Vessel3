using System.Text.Json.Serialization;

namespace Vessel3.Server.Telemetry.Otlp;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(OtlpTracePayload))]
[JsonSerializable(typeof(OtlpResourceSpans))]
[JsonSerializable(typeof(OtlpResource))]
[JsonSerializable(typeof(OtlpScopeSpans))]
[JsonSerializable(typeof(OtlpScope))]
[JsonSerializable(typeof(OtlpSpan))]
[JsonSerializable(typeof(OtlpKeyValue))]
[JsonSerializable(typeof(OtlpAnyValue))]
[JsonSerializable(typeof(OtlpStatus))]
public sealed partial class OtlpJsonContext : JsonSerializerContext;
