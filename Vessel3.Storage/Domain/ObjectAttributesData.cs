namespace Vessel3.Storage;

internal sealed record ObjectAttributesData(
    long Size,
    DateTimeOffset LastModified,
    string Etag,
    string Sha256,
    IReadOnlyList<MultipartPart>? Parts);
