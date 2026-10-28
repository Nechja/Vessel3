using System.Text.Json.Serialization;

namespace Vessel3.Storage;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PutEvent), "Put")]
[JsonDerivedType(typeof(DeleteMarkerEvent), "DeleteMarker")]
[JsonDerivedType(typeof(HardDeleteEvent), "HardDelete")]
[JsonDerivedType(typeof(PutTaggingEvent), "PutTagging")]
[JsonDerivedType(typeof(PutRetentionEvent), "PutRetention")]
[JsonDerivedType(typeof(PutLegalHoldEvent), "PutLegalHold")]
internal abstract record VersionEvent(
    long Seq,
    DateTimeOffset At,
    string Key,
    string VersionId,
    string? OriginNodeId = null,
    long OriginSeq = 0)
{
    public abstract VersionEvent WithSeq(long seq, string? defaultOriginNodeId = null);

    protected (string? NodeId, long Seq) ResolveOrigin(long seq, string? defaultOriginNodeId) =>
        !string.IsNullOrEmpty(OriginNodeId)
            ? (OriginNodeId, OriginSeq > 0 ? OriginSeq : Seq)
            : (defaultOriginNodeId, seq);

    public void ApplyTo(IBucketIndex index)
    {
        Apply(index);
        index.MarkApplied(Seq);
        if (!string.IsNullOrEmpty(OriginNodeId) && OriginSeq > 0)
            index.SetPeerCursor(OriginNodeId, OriginSeq);
    }

    protected abstract void Apply(IBucketIndex index);
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
    bool LegalHoldOn = false,
    IReadOnlyDictionary<string, string>? SystemHeaders = null,
    string? OriginNodeId = null,
    long OriginSeq = 0)
    : VersionEvent(Seq, At, Key, VersionId, OriginNodeId, OriginSeq)
{
    public override VersionEvent WithSeq(long seq, string? defaultOriginNodeId = null)
    {
        var (nodeId, originSeq) = ResolveOrigin(seq, defaultOriginNodeId);
        return this with { Seq = seq, OriginNodeId = nodeId, OriginSeq = originSeq };
    }

    protected override void Apply(IBucketIndex index) => index.Insert(this);
}

internal sealed record PutTaggingEvent(
    long Seq, DateTimeOffset At, string Key, string VersionId,
    IReadOnlyDictionary<string, string> Tags,
    string? OriginNodeId = null,
    long OriginSeq = 0)
    : VersionEvent(Seq, At, Key, VersionId, OriginNodeId, OriginSeq)
{
    public override VersionEvent WithSeq(long seq, string? defaultOriginNodeId = null)
    {
        var (nodeId, originSeq) = ResolveOrigin(seq, defaultOriginNodeId);
        return this with { Seq = seq, OriginNodeId = nodeId, OriginSeq = originSeq };
    }

    protected override void Apply(IBucketIndex index) => index.UpdateTags(Key, VersionId, Tags);
}

internal sealed record DeleteMarkerEvent(
    long Seq, DateTimeOffset At, string Key, string VersionId,
    string? OriginNodeId = null,
    long OriginSeq = 0)
    : VersionEvent(Seq, At, Key, VersionId, OriginNodeId, OriginSeq)
{
    public override VersionEvent WithSeq(long seq, string? defaultOriginNodeId = null)
    {
        var (nodeId, originSeq) = ResolveOrigin(seq, defaultOriginNodeId);
        return this with { Seq = seq, OriginNodeId = nodeId, OriginSeq = originSeq };
    }

    protected override void Apply(IBucketIndex index) => index.Insert(this);
}

internal sealed record HardDeleteEvent(
    long Seq, DateTimeOffset At, string Key, string VersionId,
    string? OriginNodeId = null,
    long OriginSeq = 0)
    : VersionEvent(Seq, At, Key, VersionId, OriginNodeId, OriginSeq)
{
    public override VersionEvent WithSeq(long seq, string? defaultOriginNodeId = null)
    {
        var (nodeId, originSeq) = ResolveOrigin(seq, defaultOriginNodeId);
        return this with { Seq = seq, OriginNodeId = nodeId, OriginSeq = originSeq };
    }

    protected override void Apply(IBucketIndex index) => index.Remove(Key, VersionId);
}

internal sealed record PutRetentionEvent(
    long Seq, DateTimeOffset At, string Key, string VersionId,
    RetentionMode Mode, long RetainUntilUnixSeconds,
    string? OriginNodeId = null,
    long OriginSeq = 0)
    : VersionEvent(Seq, At, Key, VersionId, OriginNodeId, OriginSeq)
{
    public override VersionEvent WithSeq(long seq, string? defaultOriginNodeId = null)
    {
        var (nodeId, originSeq) = ResolveOrigin(seq, defaultOriginNodeId);
        return this with { Seq = seq, OriginNodeId = nodeId, OriginSeq = originSeq };
    }

    protected override void Apply(IBucketIndex index) =>
        index.ApplyRetention(Key, VersionId, Mode, RetainUntilUnixSeconds);
}

internal sealed record PutLegalHoldEvent(
    long Seq, DateTimeOffset At, string Key, string VersionId, bool On,
    string? OriginNodeId = null,
    long OriginSeq = 0)
    : VersionEvent(Seq, At, Key, VersionId, OriginNodeId, OriginSeq)
{
    public override VersionEvent WithSeq(long seq, string? defaultOriginNodeId = null)
    {
        var (nodeId, originSeq) = ResolveOrigin(seq, defaultOriginNodeId);
        return this with { Seq = seq, OriginNodeId = nodeId, OriginSeq = originSeq };
    }

    protected override void Apply(IBucketIndex index) =>
        index.ApplyLegalHold(Key, VersionId, On);
}

internal sealed record LogRecord(long Seq, DateTimeOffset At, IReadOnlyList<VersionEvent> Ops);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(LogRecord))]
[JsonSerializable(typeof(VersionEvent))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<MultipartPart>))]
internal sealed partial class VersionEventContext : JsonSerializerContext;
