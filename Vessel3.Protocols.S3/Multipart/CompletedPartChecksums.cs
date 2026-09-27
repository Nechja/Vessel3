namespace Vessel3.Server.S3;

internal sealed record CompletedPartChecksums(string? Crc32, string? Crc32C, string? Sha1, string? Sha256);
