namespace Vessel3.Server.Storage;

internal abstract record VersionEntry(string VersionId, DateTimeOffset At);

internal sealed record PutEntry(
    string VersionId, DateTimeOffset At,
    string BlobSha, long Size, string ContentType)
    : VersionEntry(VersionId, At);

internal sealed record DeleteMarkerEntry(
    string VersionId, DateTimeOffset At)
    : VersionEntry(VersionId, At);

internal sealed record VersionListEntry(
    string Key, string VersionId, DateTimeOffset At,
    string BlobSha, long Size, string ContentType);
