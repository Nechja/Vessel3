namespace Vessel3.Storage;

internal sealed record CompleteUploadOutcome(string Etag, string VersionId, long Size, ChecksumSet Checksums);
