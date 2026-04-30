using System.Text.Json.Serialization;

namespace Vessel3.Server.Storage;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PutEvent), "Put")]
[JsonDerivedType(typeof(DeleteMarkerEvent), "DeleteMarker")]
[JsonDerivedType(typeof(HardDeleteEvent), "HardDelete")]
internal abstract record VersionEvent(long Seq, DateTimeOffset At, string Key, string VersionId)
{
    public abstract VersionEvent WithSeq(long seq);
    public abstract void ApplyTo(BucketIndex index);
}

internal sealed record PutEvent(
    long Seq, DateTimeOffset At, string Key, string VersionId,
    string BlobSha, string Md5, long Size, string ContentType)
    : VersionEvent(Seq, At, Key, VersionId)
{
    public override VersionEvent WithSeq(long seq) => this with { Seq = seq };
    public override void ApplyTo(BucketIndex index) => index.Insert(this);
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

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(VersionEvent))]
internal sealed partial class VersionEventContext : JsonSerializerContext;
