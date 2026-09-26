namespace Vessel3.Storage;

internal sealed record ObjectPutRequest(
    string Bucket,
    string Key,
    Stream Body,
    long? DeclaredSize,
    string? ContentType,
    string? DeclaredSha256,
    string? DeclaredMd5Base64,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyDictionary<string, string> Tags,
    ChecksumSet DeclaredChecksums,
    CancellationToken Ct,
    Retention? Retention = null,
    bool LegalHoldOn = false,
    IReadOnlyDictionary<string, string>? SystemHeaders = null);
