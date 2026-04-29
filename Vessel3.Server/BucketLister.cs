using System.Text;

namespace Vessel3.Server;

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

// Renders a bucket's index into S3-shaped listing pages with prefix/delimiter folding.
// Continuation token is base64(last-emitted-key) — stateless on the server.
internal sealed class BucketLister(BucketRegistry registry)
{
    public Result<ListPage> List(ListRequest req, string? continuationToken)
    {
        if (!registry.IsValidName(req.Bucket)) return new InvalidPathError(req.Bucket);

        var b = registry.Open(req.Bucket);
        if (b is null) return new NotFoundError(req.Bucket);

        var startKey = continuationToken is not null
            ? Encoding.UTF8.GetString(Convert.FromBase64String(continuationToken))
            : req.StartAfter;

        var emitted = new List<ListEntry>();
        string? lastEmittedKey = null;
        string? lastCommonPrefix = null;
        var truncated = false;

        foreach (var entry in b.Index.ListCurrent(req.Prefix, startKey))
        {
            if (emitted.Count >= req.MaxKeys)
            {
                truncated = true;
                break;
            }

            if (req.Delimiter is "/")
            {
                var prefixLen = req.Prefix?.Length ?? 0;
                var rest = entry.Key.AsSpan(prefixLen);
                var slash = rest.IndexOf('/');
                if (slash >= 0)
                {
                    var commonPrefix = (req.Prefix ?? string.Empty) + new string(rest[..(slash + 1)]);
                    if (commonPrefix == lastCommonPrefix) continue;
                    emitted.Add(new ListEntry.CommonPrefix(commonPrefix));
                    lastCommonPrefix = commonPrefix;
                    lastEmittedKey = entry.Key;
                    continue;
                }
            }

            emitted.Add(new ListEntry.Contents(entry.Key, entry.Size, entry.At, entry.BlobSha));
            lastEmittedKey = entry.Key;
        }

        var nextToken = truncated && lastEmittedKey is not null
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(lastEmittedKey))
            : null;

        return new ListPage(emitted, truncated, nextToken, emitted.Count);
    }
}
