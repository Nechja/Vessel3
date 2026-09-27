namespace Vessel3.Storage;

internal sealed record ListRequest(
    string Bucket,
    string? Prefix,
    string? Delimiter,
    string? StartAfter,
    int MaxKeys);
