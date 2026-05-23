using Vessel3.Server;

namespace Vessel3.Server.Storage;

internal abstract record VersionEntry(string VersionId, DateTimeOffset At);

internal sealed record MultipartPart(int Number, string BlobSha, string Md5, long Size,
    string? Crc32 = null, string? Crc32C = null, string? Sha1 = null);

internal sealed record DeleteOutcome(string VersionId, bool IsDeleteMarker, bool Found);

internal sealed record PutTaggingOutcome(string VersionId);

internal sealed record PutRequest(
    string BlobSha,
    string Md5,
    long Size,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<MultipartPart>? Parts = null,
    IReadOnlyDictionary<string, string>? Tags = null,
    string? Crc32 = null,
    string? Crc32C = null,
    string? Sha1 = null,
    Retention? Retention = null,
    bool LegalHoldOn = false);

internal sealed record PutEntry(
    string VersionId, DateTimeOffset At,
    string BlobSha, string Md5, long Size, string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<MultipartPart>? Parts = null,
    IReadOnlyDictionary<string, string>? Tags = null,
    string? Crc32 = null,
    string? Crc32C = null,
    string? Sha1 = null,
    Retention? Retention = null,
    bool LegalHoldOn = false)
    : VersionEntry(VersionId, At)
{
    public string WireEtag => Parts is { } p ? $"{Md5}-{p.Count}" : Md5;
    public string WireSha256 => Parts is null ? BlobSha : "";
}

internal sealed record DeleteMarkerEntry(
    string VersionId, DateTimeOffset At)
    : VersionEntry(VersionId, At);

internal sealed record VersionListEntry(
    string Key, string VersionId, DateTimeOffset At,
    string BlobSha, string Md5, long Size, string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<MultipartPart>? Parts = null)
{
    public string WireEtag => Parts is { } p ? $"{Md5}-{p.Count}" : Md5;
}

internal abstract record AllVersionsEntry(string Key, string VersionId, DateTimeOffset At, bool IsLatest)
{
    internal sealed record Put(string Key, string VersionId, DateTimeOffset At, bool IsLatest,
        string Md5, long Size, IReadOnlyList<MultipartPart>? Parts)
        : AllVersionsEntry(Key, VersionId, At, IsLatest)
    {
        public string WireEtag => Parts is { } p ? $"{Md5}-{p.Count}" : Md5;
    }

    internal sealed record Marker(string Key, string VersionId, DateTimeOffset At, bool IsLatest)
        : AllVersionsEntry(Key, VersionId, At, IsLatest);
}
