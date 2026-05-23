using System.Text.Json.Serialization;
using Vessel3.Server;

namespace Vessel3.Server.Storage;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PutEvent), "Put")]
[JsonDerivedType(typeof(DeleteMarkerEvent), "DeleteMarker")]
[JsonDerivedType(typeof(HardDeleteEvent), "HardDelete")]
[JsonDerivedType(typeof(PutTaggingEvent), "PutTagging")]
[JsonDerivedType(typeof(PutRetentionEvent), "PutRetention")]
[JsonDerivedType(typeof(PutLegalHoldEvent), "PutLegalHold")]
internal abstract record VersionEvent(long Seq, DateTimeOffset At, string Key, string VersionId)
{
    public abstract VersionEvent WithSeq(long seq);
    public abstract void ApplyTo(BucketIndex index);
}

internal sealed record PutEvent(
    long Seq, DateTimeOffset At, string Key, string VersionId,
    string BlobSha, string Md5, long Size, string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<MultipartPart>? Parts = null,
    IReadOnlyDictionary<string, string>? Tags = null,
    string? Crc32 = null,
    string? Crc32C = null,
    string? Sha1 = null,
    /// Initial retention captured at PUT time. Null when no retention applies.
    /// Older log entries (before object-lock landed) deserialize with null here.
    RetentionMode? RetentionMode = null,
    long? RetainUntilUnixSeconds = null,
    bool LegalHoldOn = false)
    : VersionEvent(Seq, At, Key, VersionId)
{
    public override VersionEvent WithSeq(long seq) => this with { Seq = seq };
    public override void ApplyTo(BucketIndex index) => index.Insert(this);
}

/// <summary>
/// Per-version tag-set mutation. Lives in the event log because tags are mutable
/// per-version metadata; the log is the single source of truth, and mutable
/// per-version state needs log-shaped writes so replay rebuilds the index exactly.
/// An empty tag dictionary represents DELETE ?tagging (the tag set is cleared).
/// </summary>
internal sealed record PutTaggingEvent(
    long Seq, DateTimeOffset At, string Key, string VersionId,
    IReadOnlyDictionary<string, string> Tags)
    : VersionEvent(Seq, At, Key, VersionId)
{
    public override VersionEvent WithSeq(long seq) => this with { Seq = seq };
    public override void ApplyTo(BucketIndex index) => index.UpdateTags(Key, VersionId, Tags);
}

internal sealed record DeleteMarkerEvent(
    long Seq, DateTimeOffset At, string Key, string VersionId)
    : VersionEvent(Seq, At, Key, VersionId)
{
    public override VersionEvent WithSeq(long seq) => this with { Seq = seq };
    public override void ApplyTo(BucketIndex index) => index.Insert(this);
}

internal sealed record HardDeleteEvent(
    long Seq, DateTimeOffset At, string Key, string VersionId)
    : VersionEvent(Seq, At, Key, VersionId)
{
    public override VersionEvent WithSeq(long seq) => this with { Seq = seq };
    public override void ApplyTo(BucketIndex index) => index.Remove(Key, VersionId);
}

/// Sets or replaces the retention on a specific version. Persisted to the
/// versions table as a row update; replay re-applies the latest such event
/// per (key, versionId).
internal sealed record PutRetentionEvent(
    long Seq, DateTimeOffset At, string Key, string VersionId,
    RetentionMode Mode, long RetainUntilUnixSeconds)
    : VersionEvent(Seq, At, Key, VersionId)
{
    public override VersionEvent WithSeq(long seq) => this with { Seq = seq };
    public override void ApplyTo(BucketIndex index) =>
        index.ApplyRetention(Key, VersionId, Mode, RetainUntilUnixSeconds);
}

/// Toggles legal hold on a specific version.
internal sealed record PutLegalHoldEvent(
    long Seq, DateTimeOffset At, string Key, string VersionId, bool On)
    : VersionEvent(Seq, At, Key, VersionId)
{
    public override VersionEvent WithSeq(long seq) => this with { Seq = seq };
    public override void ApplyTo(BucketIndex index) =>
        index.ApplyLegalHold(Key, VersionId, On);
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(VersionEvent))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<MultipartPart>))]
internal sealed partial class VersionEventContext : JsonSerializerContext;
