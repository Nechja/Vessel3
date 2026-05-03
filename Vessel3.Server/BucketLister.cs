using System.Text;
using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record ListRequest(
    string Bucket,
    string? Prefix,
    string? Delimiter,
    string? StartAfter,
    int MaxKeys,
    bool IsV1 = false,
    string? Marker = null);

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

        return registry.ListCurrent(req.Bucket, req.Prefix, startKey).Match<Result<ListPage>>(
            entries => Page(req, entries),
            err => err);
    }

    private ListPage Page(ListRequest req, IEnumerable<VersionListEntry> entries)
    {
        var emitted = new List<ListEntry>();
        string? lastEmittedKey = null;
        string? lastCommonPrefix = null;
        var truncated = false;

        foreach (var entry in entries)
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

            emitted.Add(new ListEntry.Contents(entry.Key, entry.Size, entry.At, entry.WireEtag));
            lastEmittedKey = entry.Key;
        }

        var nextToken = truncated && lastEmittedKey is not null
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(lastEmittedKey))
            : null;

        return new ListPage(emitted, truncated, nextToken, lastEmittedKey, emitted.Count);
    }
}
