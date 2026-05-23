using System.Text.Json.Serialization;

namespace Vessel3.Server;

/// Object Lock retention modes. GOVERNANCE allows authorized bypass via
/// the x-amz-bypass-governance-retention header; COMPLIANCE is unbreakable
/// for the entire retention window (no bypass, no shortening, no removal).
internal enum RetentionMode { Governance, Compliance }

/// Bucket-level Object Lock configuration. `Enabled` cannot be turned
/// off once true (S3 semantics). `Default` is the optional default
/// retention rule applied to PUTs that don't carry retention headers.
internal sealed record ObjectLockConfig(bool Enabled, ObjectLockDefault? Default);

/// A default-retention rule on the bucket. Either Days or Years is set
/// (mutually exclusive). Resolved into a concrete RetainUntilDate at PUT time.
internal sealed record ObjectLockDefault(RetentionMode Mode, int? Days, int? Years)
{
    public DateTimeOffset ResolveUntil(DateTimeOffset now) =>
        Days is { } d ? now.AddDays(d)
        : Years is { } y ? now.AddYears(y)
        : throw new InvalidOperationException("default retention with neither Days nor Years");
}

/// Per-version retention setting. Mutated by PutRetention events.
internal sealed record Retention(RetentionMode Mode, DateTimeOffset RetainUntilDate);

/// Per-version legal hold setting.
internal sealed record LegalHold(bool On);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ObjectLockConfig))]
internal sealed partial class ObjectLockJsonContext : JsonSerializerContext;
