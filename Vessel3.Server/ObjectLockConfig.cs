using System.Text.Json.Serialization;

namespace Vessel3.Server;

internal enum RetentionMode { Governance, Compliance }

internal sealed record ObjectLockConfig(bool Enabled, ObjectLockDefault? Default);

internal sealed record ObjectLockDefault(RetentionMode Mode, int? Days, int? Years)
{
    public DateTimeOffset ResolveUntil(DateTimeOffset now) =>
        Days is { } d ? now.AddDays(d)
        : Years is { } y ? now.AddYears(y)
        : throw new InvalidOperationException("default retention with neither Days nor Years");
}

internal sealed record Retention(RetentionMode Mode, DateTimeOffset RetainUntilDate);

internal sealed record LegalHold(bool On);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ObjectLockConfig))]
internal sealed partial class ObjectLockJsonContext : JsonSerializerContext;
