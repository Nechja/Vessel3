namespace Vessel3.Server.S3;

internal sealed record CompleteUploadOutcome(string Etag, string VersionId, long Size, ChecksumSet Checksums);
