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
    RetentionMode? RetentionMode = null,
    long? RetainUntilUnixSeconds = null,
    bool LegalHoldOn = false)
    : VersionEvent(Seq, At, Key, VersionId)
{
    public override VersionEvent WithSeq(long seq) => this with { Seq = seq };
    public override void ApplyTo(BucketIndex index) => index.Insert(this);
}

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

internal sealed record PutRetentionEvent(
    long Seq, DateTimeOffset At, string Key, string VersionId,
    RetentionMode Mode, long RetainUntilUnixSeconds)
    : VersionEvent(Seq, At, Key, VersionId)
{
    public override VersionEvent WithSeq(long seq) => this with { Seq = seq };
    public override void ApplyTo(BucketIndex index) =>
        index.ApplyRetention(Key, VersionId, Mode, RetainUntilUnixSeconds);
}

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
