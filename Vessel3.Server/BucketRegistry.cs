using System.Collections.Concurrent;
using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record BucketInfo(string Name, DateTimeOffset CreatedAt);
internal sealed record BucketRegistryOptions(string Root);

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
    Result<IEnumerable<VersionListEntry>> ListCurrent(string bucket, string? prefix, string? startAfter);
    Result<List<AllVersionsEntry>> ListAllVersions(string bucket, string? prefix, string? keyMarker);
    Result<VersioningStatus> GetVersioning(string bucket);
    Result<bool> SetVersioning(string bucket, VersioningStatus status);
}

internal sealed class BucketRegistry(BucketRegistryOptions options) : IBucketRegistry
{
    private readonly string bucketsRoot = Path.Combine(options.Root, "buckets");
    private readonly ConcurrentDictionary<string, Lazy<Bucket>> openBuckets = new();

    public bool IsValidName(string bucket) =>
        !string.IsNullOrEmpty(bucket)
        && !bucket.Contains('/')
        && !bucket.Contains("..", StringComparison.Ordinal);

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
        !IsValidName(bucket) ? new InvalidPathError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : Open(bucket) is { } b
            ? b.Index.GetCurrentPut(key)
            : new NotFoundError(bucket);

    public Result<PutEntry?> GetVersion(string bucket, string key, string versionId) =>
        !IsValidName(bucket) ? new InvalidPathError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : Open(bucket) is { } b
            ? b.Index.GetVersion(key, versionId)
            : new NotFoundError(bucket);

    public Result<PutEntry> AppendPut(string bucket, string key, PutRequest req) =>
        !IsValidName(bucket) ? new InvalidPathError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : Open(bucket) is { } b
            ? b.AppendPut(key, req)
            : new NotFoundError(bucket);

    public Result<DeleteOutcome> AppendDelete(string bucket, string key) =>
        !IsValidName(bucket) ? new InvalidPathError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : Open(bucket) is { } b
            ? b.AppendDelete(key)
            : new NotFoundError(bucket);

    public Result<DeleteOutcome> HardDeleteVersion(string bucket, string key, string versionId) =>
        !IsValidName(bucket) ? new InvalidPathError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : Open(bucket) is { } b
            ? b.HardDeleteVersion(key, versionId)
            : new NotFoundError(bucket);

    public Result<IEnumerable<VersionListEntry>> ListCurrent(string bucket, string? prefix, string? startAfter) =>
        !IsValidName(bucket) ? new InvalidPathError(bucket)
        : Open(bucket) is { } b
            ? new Result<IEnumerable<VersionListEntry>>.Success(b.Index.ListCurrent(prefix, startAfter))
            : new NotFoundError(bucket);

    public Result<List<AllVersionsEntry>> ListAllVersions(string bucket, string? prefix, string? keyMarker) =>
        !IsValidName(bucket) ? new InvalidPathError(bucket)
        : Open(bucket) is { } b
            ? b.Index.ListAllVersions(prefix, keyMarker)
            : new NotFoundError(bucket);

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
