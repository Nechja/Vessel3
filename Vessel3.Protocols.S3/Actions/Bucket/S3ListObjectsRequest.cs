namespace Vessel3.Server.S3;

internal sealed record S3ListObjectsRequest(
    string Bucket,
    string? Prefix,
    string? Delimiter,
    string? StartAfter,
    int MaxKeys,
    bool IsV1 = false,
    string? Marker = null,
    string? EncodingType = null)
{
    public ListRequest ToStorageRequest() =>
        new(Bucket, Prefix, Delimiter, StartAfter, MaxKeys);
}
