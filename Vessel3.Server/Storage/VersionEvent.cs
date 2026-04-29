using MemoryPack;

namespace Vessel3.Server.Storage;

internal enum EventKind : byte
{
    Put = 0,
    DeleteMarker = 1,
    HardDelete = 2,
}

[MemoryPackable]
internal sealed partial record VersionEvent(
    long Seq,
    DateTimeOffset At,
    string Key,
    string VersionId,
    string BlobSha,
    EventKind Kind,
    long Size,
    string ContentType
);
