namespace Vessel3.Server;

internal sealed record BucketInfo(string Name, DateTimeOffset CreatedAt);

internal sealed record ListRequest(
    string Bucket,
    string? Prefix,
    string? Delimiter,
    string? StartAfter,
    int MaxKeys);

internal abstract record ListEntry(string Key)
{
    internal sealed record Contents(string Key, long Size, DateTimeOffset LastModified, string Etag) : ListEntry(Key);
    internal sealed record CommonPrefix(string Key) : ListEntry(Key);
}

internal sealed record ListPage(
    IReadOnlyList<ListEntry> Entries,
    bool IsTruncated,
    string? NextContinuationToken,
    int KeyCount);
