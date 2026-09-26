namespace Vessel3.Storage;

internal sealed record UploadPartOutcome(string Etag, string BlobSha, long Size, ChecksumSet Checksums);
