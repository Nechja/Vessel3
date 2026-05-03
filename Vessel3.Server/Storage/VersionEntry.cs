namespace Vessel3.Server.Storage;

internal abstract record VersionEntry(string VersionId, DateTimeOffset At);

internal sealed record MultipartPart(int Number, string BlobSha, string Md5, long Size);

internal sealed record PutRequest(
    string BlobSha,
    string Md5,
    long Size,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<MultipartPart>? Parts = null);

internal sealed record PutEntry(
    string VersionId, DateTimeOffset At,
    string BlobSha, string Md5, long Size, string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<MultipartPart>? Parts = null)
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
