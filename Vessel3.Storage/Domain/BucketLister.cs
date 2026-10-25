namespace Vessel3.Storage;

internal sealed class BucketLister(IBucketRegistry registry) : IBucketLister
{
    public Result<ListPage> List(ListRequest req, string? continuationToken)
    {
        var startKey = req.StartAfter;
        if (continuationToken is not null)
        {
            if (!ContinuationToken.TryDecode(continuationToken, out var decodedKey))
                return new InvalidArgumentError("The continuation token provided is incorrect or malformed.");
            startKey = decodedKey;
        }
        KeyBound? from = startKey is null ? null : Resume(req, startKey);

        List<ListEntry> emitted = [];
        string? lastCommonPrefix = null;
        while (true)
        {
            if (!registry.ListCurrent(req.Bucket, req.Prefix, from, req.MaxKeys - emitted.Count).TryGetValue(out var batch, out var err))
                return err;

            foreach (var row in batch.Entries)
            {
                var prefixSpan = CommonPrefixSpanOf(req, row.Key);
                if (!prefixSpan.IsEmpty)
                {
                    if (lastCommonPrefix is not null && prefixSpan.SequenceEqual(lastCommonPrefix.AsSpan()))
                    {
                        continue;
                    }

                    var commonPrefix = prefixSpan.ToString();
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
            ? ContinuationToken.Encode(lastKey)
            : null;
        return new ListPage(emitted, truncated, nextToken, lastKey, emitted.Count);
    }

    private static KeyBound Resume(ListRequest req, string key) =>
        CommonPrefixOf(req, key) == key && KeyRange.Successor(key) is { } next ? KeyBound.From(next) : KeyBound.After(key);

    private static ReadOnlySpan<char> CommonPrefixSpanOf(ListRequest req, string key)
    {
        if (req.Delimiter is not "/") return default;
        var prefix = req.Prefix ?? string.Empty;
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return default;
        var slash = key.IndexOf('/', prefix.Length);
        return slash < 0 ? default : key.AsSpan(0, slash + 1);
    }

    private static string? CommonPrefixOf(ListRequest req, string key)
    {
        var span = CommonPrefixSpanOf(req, key);
        return span.IsEmpty ? null : span.ToString();
    }
}
