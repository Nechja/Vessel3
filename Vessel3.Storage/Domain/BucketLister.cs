using System.Text;

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

internal abstract record ListEntry(string Key)
{
    internal sealed record Contents(string Key, long Size, DateTimeOffset LastModified, string Etag) : ListEntry(Key);
    internal sealed record CommonPrefix(string Key) : ListEntry(Key);
}

internal sealed record ListPage(
    IReadOnlyList<ListEntry> Entries,
    bool IsTruncated,
    string? NextContinuationToken,
    string? LastKey,
    int KeyCount);

internal interface IBucketLister
{
    Result<ListPage> List(ListRequest req, string? continuationToken);
}

internal sealed class BucketLister(IBucketRegistry registry) : IBucketLister
{
    public Result<ListPage> List(ListRequest req, string? continuationToken)
    {
        var startKey = continuationToken is not null
            ? Encoding.UTF8.GetString(Convert.FromBase64String(continuationToken))
            : req.StartAfter;
        KeyBound? from = startKey is null ? null : Resume(req, startKey);

        List<ListEntry> emitted = [];
        string? lastCommonPrefix = null;
        while (true)
        {
            if (!registry.ListCurrent(req.Bucket, req.Prefix, from, req.MaxKeys - emitted.Count).TryGetValue(out var batch, out var err))
                return err;

            foreach (var row in batch.Entries)
            {
                if (CommonPrefixOf(req, row.Key) is { } commonPrefix)
                {
                    if (commonPrefix == lastCommonPrefix) continue;
                    emitted.Add(new ListEntry.CommonPrefix(commonPrefix));
                    lastCommonPrefix = commonPrefix;
                }
                else
                {
                    emitted.Add(new ListEntry.Contents(row.Key, row.Size, row.At, row.WireEtag));
                }
            }

            if (!batch.IsTruncated) return Page(emitted, truncated: false);
            if (batch.Entries.Count is 0) return Page(emitted, truncated: true);
            if (emitted.Count == req.MaxKeys && emitted[^1] is ListEntry.Contents) return Page(emitted, truncated: true);
            from = Resume(req, emitted[^1].Key);
        }
    }

    private static ListPage Page(List<ListEntry> emitted, bool truncated)
    {
        var lastKey = emitted.Count is 0 ? null : emitted[^1].Key;
        var nextToken = truncated && lastKey is not null
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(lastKey))
            : null;
        return new ListPage(emitted, truncated, nextToken, lastKey, emitted.Count);
    }

    private static KeyBound Resume(ListRequest req, string key) =>
        CommonPrefixOf(req, key) == key && KeyRange.Successor(key) is { } next ? KeyBound.From(next) : KeyBound.After(key);

    private static string? CommonPrefixOf(ListRequest req, string key)
    {
        if (req.Delimiter is not "/") return null;
        var prefix = req.Prefix ?? string.Empty;
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var slash = key.IndexOf('/', prefix.Length);
        return slash < 0 ? null : key[..(slash + 1)];
    }
}
