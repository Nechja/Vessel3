namespace Vessel3.Server.S3;

internal sealed record UploadPartOutcome(string Etag, string BlobSha, long Size, ChecksumSet Checksums);
