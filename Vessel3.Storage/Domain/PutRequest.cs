namespace Vessel3.Storage;

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
    bool LegalHoldOn = false,
    IReadOnlyDictionary<string, string>? SystemHeaders = null);
