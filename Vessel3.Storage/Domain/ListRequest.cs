namespace Vessel3.Storage;

internal sealed record ListRequest(
    string Bucket,
    string? Prefix,
    string? Delimiter,
    string? StartAfter,
    int MaxKeys,
    bool IsV1 = false,
    string? Marker = null,
    string? EncodingType = null);
