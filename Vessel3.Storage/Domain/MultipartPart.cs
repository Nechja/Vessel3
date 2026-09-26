namespace Vessel3.Storage;

internal sealed record MultipartPart(int Number, string BlobSha, string Md5, long Size,
    string? Crc32 = null, string? Crc32C = null, string? Sha1 = null);
