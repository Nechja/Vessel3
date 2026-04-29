using System.Collections.Concurrent;
using System.Text;
using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record PutOutcome(string Etag, string VersionId, long Size);

internal sealed record StoredObject(FileStream Body, long Size, DateTimeOffset LastModified, string Etag, string ContentType);

internal sealed record ObjectStat(long Size, DateTimeOffset LastModified, string Etag, string ContentType);

internal sealed class FileObjectStore(string root, BlobPool blobs) : IDisposable
{
    private readonly string bucketsRoot = Path.Combine(root, "buckets");
    private readonly ConcurrentDictionary<string, Lazy<Bucket>> openBuckets = new();

    public Task<Result<PutOutcome>> Put(string bucket, string key, Stream body, long? declaredSize, string? contentType, CancellationToken ct) =>
        !IsValidBucketName(bucket) ? Task.FromResult<Result<PutOutcome>>(new InvalidPathError(bucket))
        : string.IsNullOrEmpty(key) ? Task.FromResult<Result<PutOutcome>>(new InvalidPathError($"{bucket}/{key}"))
        : OpenBucket(bucket) is { } b
            ? PutInto(b, key, body, declaredSize, contentType, ct)
            : Task.FromResult<Result<PutOutcome>>(new NotFoundError(bucket));

    public Result<StoredObject> Get(string bucket, string key) =>
        !IsValidBucketName(bucket) ? new InvalidPathError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : OpenBucket(bucket) is { } b
            ? GetFrom(b, bucket, key)
            : new NotFoundError(bucket);

    public Result<ObjectStat> Stat(string bucket, string key) =>
        !IsValidBucketName(bucket) ? new InvalidPathError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : OpenBucket(bucket) is { } b
            ? StatFrom(b, bucket, key)
            : new NotFoundError(bucket);

    public Result<bool> Delete(string bucket, string key) =>
        !IsValidBucketName(bucket) ? new InvalidPathError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : OpenBucket(bucket)?.AppendHardDeleteCurrent(key) ?? false;

    private async Task<Result<PutOutcome>> PutInto(Bucket b, string key, Stream body, long? declaredSize, string? contentType, CancellationToken ct) =>
        await blobs.Write(body, declaredSize, ct) switch
        {
            Result<StoredBlob>.Success ok => RecordPut(b, key, ok.Value, contentType),
            Result<StoredBlob>.Failure bf => bf.Error,
            _ => throw new System.Diagnostics.UnreachableException(),
        };

    private PutOutcome RecordPut(Bucket b, string key, StoredBlob blob, string? contentType)
    {
        var resolvedContentType = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType;
        var entry = b.AppendPut(key, blob.Sha, blob.Size, resolvedContentType);
        return new PutOutcome(blob.Sha, entry.VersionId, blob.Size);
    }

    private Result<StoredObject> GetFrom(Bucket b, string bucket, string key) =>
        b.Index.GetCurrent(key) is Result<VersionEntry?>.Success { Value: { } entry }
        && entry.Kind != EventKind.DeleteMarker
            ? OpenBlob(entry)
            : new NotFoundError($"{bucket}/{key}");

    private Result<StoredObject> OpenBlob(VersionEntry entry) =>
        blobs.Open(entry.BlobSha) switch
        {
            Result<FileStream>.Success ok =>
                new StoredObject(ok.Value, entry.Size, entry.At, entry.BlobSha, entry.ContentType),
            Result<FileStream>.Failure of => of.Error,
            _ => throw new System.Diagnostics.UnreachableException(),
        };

    private Result<ObjectStat> StatFrom(Bucket b, string bucket, string key) =>
        b.Index.GetCurrent(key) is Result<VersionEntry?>.Success { Value: { } entry }
        && entry.Kind != EventKind.DeleteMarker
            ? new ObjectStat(entry.Size, entry.At, entry.BlobSha, entry.ContentType)
            : new NotFoundError($"{bucket}/{key}");

    public Result<bool> CreateBucket(string bucket)
    {
        if (!IsValidBucketName(bucket)) return new InvalidPathError(bucket);

        var path = Path.Combine(bucketsRoot, bucket);
        if (Directory.Exists(path)) return false;

        Directory.CreateDirectory(path);
        OpenBucket(bucket);
        return true;
    }

    public Result<bool> DeleteBucket(string bucket)
    {
        if (!IsValidBucketName(bucket)) return new InvalidPathError(bucket);

        var path = Path.Combine(bucketsRoot, bucket);
        if (!Directory.Exists(path)) return new NotFoundError(bucket);

        var b = OpenBucket(bucket);
        if (b is not null && !b.IsEmpty()) return new BucketNotEmptyError(bucket);

        if (openBuckets.TryRemove(bucket, out var lazy) && lazy.IsValueCreated)
            lazy.Value.Dispose();

        Directory.Delete(path, recursive: true);
        return true;
    }

    public Result<bool> BucketExists(string bucket) =>
        IsValidBucketName(bucket)
            ? Directory.Exists(Path.Combine(bucketsRoot, bucket))
            : new InvalidPathError(bucket);

    public IEnumerable<BucketInfo> ListBuckets()
    {
        if (!Directory.Exists(bucketsRoot)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(bucketsRoot).OrderBy(d => d, StringComparer.Ordinal))
            yield return new BucketInfo(Path.GetFileName(dir), Directory.GetCreationTimeUtc(dir));
    }

    public Result<ListPage> ListObjects(ListRequest req, string? continuationToken)
    {
        if (!IsValidBucketName(req.Bucket)) return new InvalidPathError(req.Bucket);

        var b = OpenBucket(req.Bucket);
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

            if (req.Delimiter == "/")
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

    public void Dispose()
    {
        foreach (var lazy in openBuckets.Values)
            if (lazy.IsValueCreated) lazy.Value.Dispose();
        openBuckets.Clear();
    }

    private Bucket? OpenBucket(string bucket)
    {
        var path = Path.Combine(bucketsRoot, bucket);
        if (!Directory.Exists(path)) return null;

        var lazy = openBuckets.GetOrAdd(bucket, _ => new Lazy<Bucket>(() =>
        {
            var b = new Bucket(bucket, path);
            b.Open();
            return b;
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    private bool IsValidBucketName(string bucket) =>
        !string.IsNullOrEmpty(bucket)
        && !bucket.Contains('/')
        && !bucket.Contains("..", StringComparison.Ordinal);
}
