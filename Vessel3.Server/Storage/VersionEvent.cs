using System.Text.Json.Serialization;

namespace Vessel3.Server.Storage;

internal enum EventKind
{
    Put = 0,
    DeleteMarker = 1,
    HardDelete = 2,
}

internal sealed record VersionEvent(
    long Seq,
    DateTimeOffset At,
    string Key,
    string VersionId,
    string BlobSha,
    EventKind Kind,
    long Size,
    string ContentType
);

[JsonSourceGenerationOptions(UseStringEnumConverter = true, WriteIndented = false)]
[JsonSerializable(typeof(VersionEvent))]
internal sealed partial class VersionEventContext : JsonSerializerContext;
