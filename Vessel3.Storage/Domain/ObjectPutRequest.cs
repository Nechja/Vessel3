namespace Vessel3.Storage;

internal sealed record ObjectPutRequest(
    string Bucket,
    string Key,
    Stream Body,
    long? DeclaredSize = null,
    string? ContentType = null,
    string? DeclaredSha256 = null,
    string? DeclaredMd5Base64 = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyDictionary<string, string>? Tags = null,
    DeclaredChecksums? DeclaredChecksums = null,
    Retention? Retention = null,
    bool LegalHoldOn = false,
    IReadOnlyDictionary<string, string>? SystemHeaders = null,
    string? Protocol = null,
    string? Actor = null,
    string? Host = null,
    CancellationToken Ct = default)
{
    public DeclaredChecksums Checksums => DeclaredChecksums ?? DeclaredChecksums.Empty;
}
