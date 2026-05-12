using System.Collections.Concurrent;
using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record BucketInfo(string Name, DateTimeOffset CreatedAt);
internal sealed record BucketRegistryOptions(string Root);
internal sealed record VersionsPage(IReadOnlyList<AllVersionsEntry> Entries, bool IsTruncated);

internal interface IBucketRegistry : IDisposable
{
    bool IsValidName(string bucket);

    Result<bool> Create(string bucket);
    Result<bool> Delete(string bucket);
    Result<bool> Exists(string bucket);
    IEnumerable<BucketInfo> List();

    Result<PutEntry?> GetCurrentPut(string bucket, string key);
    Result<PutEntry?> GetVersion(string bucket, string key, string versionId);
    Result<PutEntry> AppendPut(string bucket, string key, PutRequest req);
    Result<DeleteOutcome> AppendDelete(string bucket, string key);
    Result<DeleteOutcome> HardDeleteVersion(string bucket, string key, string versionId);
    Result<List<VersionListEntry>> ListCurrent(string bucket, string? prefix, string? startAfter);
    Result<VersionsPage> ListAllVersions(string bucket, string? prefix, string? keyMarker, int limit);
    Result<VersioningStatus> GetVersioning(string bucket);
    Result<bool> SetVersioning(string bucket, VersioningStatus status);
    IEnumerable<string> AllReferencedBlobs();
}

internal sealed class BucketRegistry(BucketRegistryOptions options) : IBucketRegistry
{
    private readonly string bucketsRoot = Path.Combine(options.Root, "buckets");
    private readonly ConcurrentDictionary<string, Lazy<Bucket>> openBuckets = new();

    public bool IsValidName(string bucket)
    {
        if (string.IsNullOrEmpty(bucket) || bucket.Length is < 3 or > 63) return false;
        if (bucket[0] is '-' or '.' || bucket[^1] is '-' or '.') return false;
        if (bucket.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var c in bucket)
        {
            var ok = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.';
            if (!ok) return false;
        }
        return true;
    }

    public Result<bool> Create(string bucket)
    {
        if (!IsValidName(bucket)) return new InvalidPathError(bucket);

        var path = Path.Combine(bucketsRoot, bucket);
        if (Directory.Exists(path)) return false;

        Directory.CreateDirectory(path);
        Open(bucket);
        return true;
    }

    public Result<bool> Delete(string bucket)
    {
        if (!IsValidName(bucket)) return new InvalidPathError(bucket);

        var path = Path.Combine(bucketsRoot, bucket);
        if (!Directory.Exists(path)) return new NotFoundError(bucket);

        var b = Open(bucket);
        if (b is not null && !b.IsEmpty()) return new BucketNotEmptyError(bucket);

        if (openBuckets.TryRemove(bucket, out var lazy) && lazy.IsValueCreated)
            lazy.Value.Dispose();

        Directory.Delete(path, recursive: true);
        return true;
    }

    public Result<bool> Exists(string bucket) =>
        IsValidName(bucket)
            ? Directory.Exists(Path.Combine(bucketsRoot, bucket))
            : new InvalidPathError(bucket);

    public IEnumerable<BucketInfo> List()
    {
        if (!Directory.Exists(bucketsRoot)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(bucketsRoot).OrderBy(d => d, StringComparer.Ordinal))
            yield return new BucketInfo(Path.GetFileName(dir), Directory.GetCreationTimeUtc(dir));
    }

    public Result<PutEntry?> GetCurrentPut(string bucket, string key) =>
        OnKey(bucket, key, b => b.Index.GetCurrentPut(key));

    public Result<PutEntry?> GetVersion(string bucket, string key, string versionId) =>
        OnKey(bucket, key, b => b.Index.GetVersion(key, versionId));

    public Result<PutEntry> AppendPut(string bucket, string key, PutRequest req) =>
        OnKey<PutEntry>(bucket, key, b => b.AppendPut(key, req));

    public Result<DeleteOutcome> AppendDelete(string bucket, string key) =>
        OnKey<DeleteOutcome>(bucket, key, b => b.AppendDelete(key));

    public Result<DeleteOutcome> HardDeleteVersion(string bucket, string key, string versionId) =>
        OnKey<DeleteOutcome>(bucket, key, b => b.HardDeleteVersion(key, versionId));

    public Result<List<VersionListEntry>> ListCurrent(string bucket, string? prefix, string? startAfter) =>
        OnBucket<List<VersionListEntry>>(bucket, b => b.Index.ListCurrent(prefix, startAfter));

    private Result<T> OnBucket<T>(string bucket, Func<Bucket, Result<T>> body) =>
        !IsValidName(bucket) ? new InvalidPathError(bucket)
        : Open(bucket) is { } b ? body(b)
        : new NotFoundError(bucket);

    private Result<T> OnKey<T>(string bucket, string key, Func<Bucket, Result<T>> body) =>
        !IsValidName(bucket) ? new InvalidPathError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : Open(bucket) is { } b ? body(b)
        : new NotFoundError(bucket);

    public Result<VersionsPage> ListAllVersions(string bucket, string? prefix, string? keyMarker, int limit)
    {
        if (!IsValidName(bucket)) return new InvalidPathError(bucket);
        if (Open(bucket) is not { } b) return new NotFoundError(bucket);
        var (entries, truncated) = b.Index.ListAllVersions(prefix, keyMarker, limit);
        return new VersionsPage(entries, truncated);
    }

    public Result<VersioningStatus> GetVersioning(string bucket) =>
        !IsValidName(bucket) ? new InvalidPathError(bucket)
        : Open(bucket) is { } b ? b.Versioning
        : (Result<VersioningStatus>)new NotFoundError(bucket);

    public Result<bool> SetVersioning(string bucket, VersioningStatus status)
    {
        if (!IsValidName(bucket)) return new InvalidPathError(bucket);
        var b = Open(bucket);
        if (b is null) return new NotFoundError(bucket);
        b.SetVersioning(status);
        return true;
    }

    public IEnumerable<string> AllReferencedBlobs()
    {
        foreach (var info in List())
        {
            var b = Open(info.Name);
            if (b is null) continue;
            foreach (var sha in b.Index.ReferencedBlobs())
                yield return sha;
        }
    }

    public void Dispose()
    {
        foreach (var lazy in openBuckets.Values)
            if (lazy.IsValueCreated) lazy.Value.Dispose();
        openBuckets.Clear();
    }

    private Bucket? Open(string bucket)
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
}
