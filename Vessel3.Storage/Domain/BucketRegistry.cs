using System.Buffers;
using System.Collections.Concurrent;

namespace Vessel3.Storage;

internal sealed record BucketInfo(string Name, DateTimeOffset CreatedAt);
internal sealed record BucketRegistryOptions(string Root);
internal sealed record VersionsPage(IReadOnlyList<AllVersionsEntry> Entries, bool IsTruncated);
internal sealed record CurrentPage(IReadOnlyList<VersionListEntry> Entries, bool IsTruncated);

internal interface IBucketRegistry : IDisposable
{
    bool IsValidName(string bucket);

    Result<bool> Create(string bucket);
    Result Delete(string bucket);
    Result<bool> Exists(string bucket);
    IEnumerable<BucketInfo> List();

    Result<PutEntry?> GetCurrentPut(string bucket, string key);
    Result<PutEntry?> GetVersion(string bucket, string key, string versionId);
    Result<PutEntry> AppendPut(string bucket, string key, PutRequest req);
    Result<DeleteOutcome> AppendDelete(string bucket, string key, bool bypassGovernance);
    Result<DeleteOutcome> HardDeleteVersion(string bucket, string key, string versionId, bool bypassGovernance);
    Result<IReadOnlyList<Result<DeleteOutcome>>> DeleteBatch(string bucket, IReadOnlyList<BatchDeleteItem> items);
    Result<CurrentPage> ListCurrent(string bucket, string? prefix, KeyBound? from, int limit);
    Result<VersionsPage> ListAllVersions(string bucket, string? prefix, string? keyMarker, int limit);
    Result<VersioningStatus> GetVersioning(string bucket);
    Result SetVersioning(string bucket, VersioningStatus status);
    Result<PutTaggingOutcome> PutTagging(string bucket, string key, string? versionId, IReadOnlyDictionary<string, string> tags);
    VersionKind? GetCurrentKind(string bucket, string key);
    VersionKind? GetVersionKind(string bucket, string key, string versionId);
    Result<ObjectLockConfig?> GetObjectLock(string bucket);
    Result SetObjectLock(string bucket, ObjectLockConfig cfg);
    Result<LifecycleConfig?> GetLifecycle(string bucket);
    Result SetLifecycle(string bucket, LifecycleConfig cfg);
    Result RemoveLifecycle(string bucket);
    Result<WebsiteConfig?> GetWebsite(string bucket);
    Result SetWebsite(string bucket, WebsiteConfig cfg);
    Result RemoveWebsite(string bucket);
    Result<BucketAccess> GetAccess(string bucket);
    Result SetAccess(string bucket, BucketAccess access);
    Result<CorsConfig?> GetCors(string bucket);
    Result SetCors(string bucket, CorsConfig cfg);
    Result RemoveCors(string bucket);
    IEnumerable<Bucket> OpenBuckets();
    Result PutRetention(string bucket, string key, string versionId, Retention retention, bool bypassGovernance);
    Result<Retention?> GetRetention(string bucket, string key, string versionId);
    Result PutLegalHold(string bucket, string key, string versionId, bool on);
    Result<bool> GetLegalHold(string bucket, string key, string versionId);
    IEnumerable<string> AllReferencedBlobs();
}

internal sealed class BucketRegistry(BucketRegistryOptions options, IFileSync fileSync, IDurableWrite durableWrite) : IBucketRegistry
{
    private readonly string bucketsRoot = Path.Combine(options.Root, "buckets");
    private readonly ConcurrentDictionary<string, Lazy<Bucket>> openBuckets = new();
    private readonly Lock createDeleteGate = new();

    private static readonly SearchValues<char> ValidBucketChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-.");

    public bool IsValidName(string bucket) =>
        !string.IsNullOrEmpty(bucket)
        && bucket.Length is >= 3 and <= 63
        && bucket[0] is not ('-' or '.')
        && bucket[^1] is not ('-' or '.')
        && !bucket.Contains("..", StringComparison.Ordinal)
        && !bucket.ContainsAnyExcept(ValidBucketChars);

    public Result<bool> Create(string bucket)
    {
        if (!IsValidName(bucket)) return new InvalidBucketNameError(bucket);

        var path = Path.Combine(bucketsRoot, bucket);
        lock (createDeleteGate)
        {
            if (Directory.Exists(path)) return false;
            if (fileSync.CreateDirectoryDurable(path) is Result.Failure f) return f.Error;
            OpenLocked(bucket, path);
        }
        return true;
    }

    public Result Delete(string bucket)
    {
        if (!IsValidName(bucket)) return new InvalidBucketNameError(bucket);

        var path = Path.Combine(bucketsRoot, bucket);
        lock (createDeleteGate)
        {
            if (!Directory.Exists(path)) return new NoSuchBucketError(bucket);

            var b = OpenLocked(bucket, path);
            if (b is not null && !b.TrySealForDelete()) return new BucketNotEmptyError(bucket);

            if (openBuckets.TryRemove(bucket, out var lazy) && lazy.IsValueCreated)
                lazy.Value.Dispose();

            Directory.Delete(path, recursive: true);
        }
        return Result.Ok;
    }

    public Result<bool> Exists(string bucket) =>
        IsValidName(bucket)
            ? Directory.Exists(Path.Combine(bucketsRoot, bucket))
            : new InvalidBucketNameError(bucket);

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

    public Result<DeleteOutcome> AppendDelete(string bucket, string key, bool bypassGovernance) =>
        OnKey<DeleteOutcome>(bucket, key, b => b.AppendDelete(key, bypassGovernance));

    public Result<DeleteOutcome> HardDeleteVersion(string bucket, string key, string versionId, bool bypassGovernance) =>
        OnKey<DeleteOutcome>(bucket, key, b => b.HardDeleteVersion(key, versionId, bypassGovernance));

    public Result<IReadOnlyList<Result<DeleteOutcome>>> DeleteBatch(string bucket, IReadOnlyList<BatchDeleteItem> items) =>
        OnBucket<IReadOnlyList<Result<DeleteOutcome>>>(bucket,
            b => new Result<IReadOnlyList<Result<DeleteOutcome>>>.Success(b.AppendDeleteBatch(items)));

    public Result<ObjectLockConfig?> GetObjectLock(string bucket) =>
        OnBucketRaw<ObjectLockConfig?>(bucket, b => b.ObjectLock);

    public Result SetObjectLock(string bucket, ObjectLockConfig cfg) =>
        OnBucket(bucket, b => b.SetObjectLock(cfg));

    public Result<LifecycleConfig?> GetLifecycle(string bucket) =>
        OnBucketRaw<LifecycleConfig?>(bucket, b => b.Lifecycle);

    public Result SetLifecycle(string bucket, LifecycleConfig cfg) =>
        OnBucket(bucket, b => b.SetLifecycle(cfg));

    public Result RemoveLifecycle(string bucket) =>
        OnBucket(bucket, b => b.RemoveLifecycle());

    public Result<WebsiteConfig?> GetWebsite(string bucket) =>
        OnBucketRaw<WebsiteConfig?>(bucket, b => b.Website);

    public Result SetWebsite(string bucket, WebsiteConfig cfg) =>
        OnBucket(bucket, b => b.SetWebsite(cfg));

    public Result RemoveWebsite(string bucket) =>
        OnBucket(bucket, b => b.RemoveWebsite());

    public Result<BucketAccess> GetAccess(string bucket) =>
        OnBucketRaw(bucket, b => b.Access);

    public Result SetAccess(string bucket, BucketAccess access) =>
        OnBucket(bucket, b => b.SetAccess(access));

    public Result<CorsConfig?> GetCors(string bucket) =>
        OnBucketRaw<CorsConfig?>(bucket, b => b.Cors);

    public Result SetCors(string bucket, CorsConfig cfg) =>
        OnBucket(bucket, b => b.SetCors(cfg));

    public Result RemoveCors(string bucket) =>
        OnBucket(bucket, b => b.RemoveCors());

    public IEnumerable<Bucket> OpenBuckets()
    {
        foreach (var info in List())
            if (Open(info.Name) is { } b) yield return b;
    }

    public Result PutRetention(string bucket, string key, string versionId, Retention retention, bool bypassGovernance) =>
        OnKey(bucket, key, b => b.PutRetention(key, versionId, retention, bypassGovernance));

    public Result<Retention?> GetRetention(string bucket, string key, string versionId) =>
        OnKey<Retention?>(bucket, key, b =>
        {
            var (r, _) = b.Index.GetLock(key, versionId);
            return r;
        });

    public Result PutLegalHold(string bucket, string key, string versionId, bool on) =>
        OnKey(bucket, key, b => b.PutLegalHold(key, versionId, on));

    public Result<bool> GetLegalHold(string bucket, string key, string versionId) =>
        OnKey<bool>(bucket, key, b =>
        {
            var (_, h) = b.Index.GetLock(key, versionId);
            return h;
        });

    public Result<CurrentPage> ListCurrent(string bucket, string? prefix, KeyBound? from, int limit) =>
        OnBucketRaw(bucket, b =>
        {
            var (entries, truncated) = b.Index.ListCurrent(prefix, from, limit);
            return new CurrentPage(entries, truncated);
        });

    private Result<T> OnBucketRaw<T>(string bucket, Func<Bucket, T> body) =>
        !IsValidName(bucket) ? new InvalidBucketNameError(bucket)
        : Open(bucket) is { } b ? body(b)
        : (Result<T>)new NoSuchBucketError(bucket);

    private Result<T> OnBucket<T>(string bucket, Func<Bucket, Result<T>> body) =>
        !IsValidName(bucket) ? new InvalidBucketNameError(bucket)
        : Open(bucket) is { } b ? body(b)
        : new NoSuchBucketError(bucket);

    private Result OnBucket(string bucket, Func<Bucket, Result> body) =>
        !IsValidName(bucket) ? new InvalidBucketNameError(bucket)
        : Open(bucket) is { } b ? body(b)
        : new NoSuchBucketError(bucket);

    private Result<T> OnKey<T>(string bucket, string key, Func<Bucket, Result<T>> body) =>
        !IsValidName(bucket) ? new InvalidBucketNameError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : Open(bucket) is { } b ? body(b)
        : new NoSuchBucketError(bucket);

    private Result OnKey(string bucket, string key, Func<Bucket, Result> body) =>
        !IsValidName(bucket) ? new InvalidBucketNameError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : Open(bucket) is { } b ? body(b)
        : new NoSuchBucketError(bucket);

    public Result<VersionsPage> ListAllVersions(string bucket, string? prefix, string? keyMarker, int limit)
    {
        if (!IsValidName(bucket)) return new InvalidBucketNameError(bucket);
        if (Open(bucket) is not { } b) return new NoSuchBucketError(bucket);
        var (entries, truncated) = b.Index.ListAllVersions(prefix, keyMarker, limit);
        return new VersionsPage(entries, truncated);
    }

    public Result<VersioningStatus> GetVersioning(string bucket) =>
        !IsValidName(bucket) ? new InvalidBucketNameError(bucket)
        : Open(bucket) is { } b ? b.Versioning
        : (Result<VersioningStatus>)new NoSuchBucketError(bucket);

    public Result<PutTaggingOutcome> PutTagging(string bucket, string key, string? versionId, IReadOnlyDictionary<string, string> tags) =>
        OnKey<PutTaggingOutcome>(bucket, key, b =>
        {
            var resolved = versionId;
            if (resolved is null)
            {
                if (b.Index.GetCurrentPut(key) is not Result<PutEntry?>.Success { Value: { } cur })
                    return new NoSuchKeyError(key);
                resolved = cur.VersionId;
            }
            else
            {
                if (b.Index.GetVersion(key, resolved) is not Result<PutEntry?>.Success { Value: not null })
                    return new NoSuchKeyError(key);
            }
            return b.AppendPutTagging(key, resolved, tags);
        });

    public VersionKind? GetCurrentKind(string bucket, string key) =>
        !IsValidName(bucket) || string.IsNullOrEmpty(key)
            ? null
            : Open(bucket) is { } b ? b.Index.GetCurrentKind(key) : null;

    public VersionKind? GetVersionKind(string bucket, string key, string versionId) =>
        !IsValidName(bucket) || string.IsNullOrEmpty(key)
            ? null
            : Open(bucket) is { } b ? b.Index.GetVersionKind(key, versionId) : null;

    public Result SetVersioning(string bucket, VersioningStatus status) =>
        OnBucket(bucket, b => b.SetVersioning(status));

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
        if (openBuckets.TryGetValue(bucket, out var existing))
            return ResolveEvictingOnFault(bucket, existing);

        var path = Path.Combine(bucketsRoot, bucket);
        if (!Directory.Exists(path)) return null;

        lock (createDeleteGate)
            return OpenLocked(bucket, path);
    }

    private Bucket? OpenLocked(string bucket, string path)
    {
        if (!Directory.Exists(path)) return null;

        var lazy = openBuckets.GetOrAdd(bucket, _ => new Lazy<Bucket>(() =>
        {
            var b = new Bucket(bucket, path, fileSync, durableWrite);
            b.Open();
            return b;
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        return ResolveEvictingOnFault(bucket, lazy);
    }

    private Bucket? ResolveEvictingOnFault(string bucket, Lazy<Bucket> lazy)
    {
        try
        {
            return lazy.Value;
        }
        catch
        {
            openBuckets.TryRemove(new KeyValuePair<string, Lazy<Bucket>>(bucket, lazy));
            throw;
        }
    }
}
