namespace Vessel3.Storage;

internal sealed record PutOutcome(string Etag, string Sha256, string VersionId, long Size, ChecksumSet Checksums);
