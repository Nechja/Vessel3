namespace Vessel3.Storage;

internal sealed record ObjectStat(
    long Size,
    DateTimeOffset LastModified,
    string Etag,
    string Sha256,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    ChecksumSet Checksums,
    IReadOnlyDictionary<string, string>? SystemHeaders = null);
