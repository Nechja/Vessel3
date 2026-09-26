using Vessel3.Storage;

namespace Vessel3.Server.S3;

internal sealed record ObjectAttributesRequest(
    bool WantEtag, string? Etag,
    bool WantChecksum, string? ChecksumSha256Base64,
    bool WantObjectParts, IReadOnlyList<MultipartPart>? Parts,
    bool WantStorageClass,
    bool WantObjectSize, long Size);
