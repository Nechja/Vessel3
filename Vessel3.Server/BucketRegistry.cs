using System.Collections.Concurrent;
using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record BucketInfo(string Name, DateTimeOffset CreatedAt);

internal interface IBucketRegistry : IDisposable
{
    bool IsValidName(string bucket);
    Result<bool> Create(string bucket);
    Result<bool> Delete(string bucket);
    Result<bool> Exists(string bucket);
    IEnumerable<BucketInfo> List();
    Bucket? Open(string bucket);
}

internal sealed class BucketRegistry(string root) : IBucketRegistry
{
    private readonly string bucketsRoot = Path.Combine(root, "buckets");
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

    public Bucket? Open(string bucket)
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

    public void Dispose()
    {
        foreach (var lazy in openBuckets.Values)
            if (lazy.IsValueCreated) lazy.Value.Dispose();
        openBuckets.Clear();
    }
}
