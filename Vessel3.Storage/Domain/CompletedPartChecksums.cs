namespace Vessel3.Storage;

internal sealed record CompletedPartChecksums(string? Crc32, string? Crc32C, string? Sha1, string? Sha256);
