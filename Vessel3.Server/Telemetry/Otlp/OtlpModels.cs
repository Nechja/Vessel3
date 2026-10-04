using System.Text.Json.Serialization;

namespace Vessel3.Server.Telemetry.Otlp;

public sealed record OtlpTracePayload(
    [property: JsonPropertyName("resourceSpans")] IReadOnlyList<OtlpResourceSpans> ResourceSpans);

public sealed record OtlpResourceSpans(
    [property: JsonPropertyName("resource")] OtlpResource Resource,
    [property: JsonPropertyName("scopeSpans")] IReadOnlyList<OtlpScopeSpans> ScopeSpans);

public sealed record OtlpResource(
    [property: JsonPropertyName("attributes")] IReadOnlyList<OtlpKeyValue> Attributes);

public sealed record OtlpScopeSpans(
    [property: JsonPropertyName("scope")] OtlpScope Scope,
    [property: JsonPropertyName("spans")] IReadOnlyList<OtlpSpan> Spans);

public sealed record OtlpScope(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version = null);

public sealed record OtlpSpan(
    [property: JsonPropertyName("traceId")] string TraceId,
    [property: JsonPropertyName("spanId")] string SpanId,
    [property: JsonPropertyName("parentSpanId")] string? ParentSpanId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("startTimeUnixNano")] string StartTimeUnixNano,
    [property: JsonPropertyName("endTimeUnixNano")] string EndTimeUnixNano,
    [property: JsonPropertyName("attributes")] IReadOnlyList<OtlpKeyValue> Attributes,
    [property: JsonPropertyName("status")] OtlpStatus Status);

public sealed record OtlpKeyValue(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] OtlpAnyValue Value);

public sealed record OtlpAnyValue(
    [property: JsonPropertyName("stringValue")] string? StringValue = null,
    [property: JsonPropertyName("intValue")] string? IntValue = null,
    [property: JsonPropertyName("boolValue")] bool? BoolValue = null,
    [property: JsonPropertyName("doubleValue")] double? DoubleValue = null);

public sealed record OtlpStatus(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string? Message = null);
