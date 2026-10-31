using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Vessel3.Storage;

internal sealed record BucketInfo(string Name, DateTimeOffset CreatedAt, string? OwnerId = null);
internal sealed record BucketRegistryOptions(string Root, string? NodeId = null);
internal sealed record VersionsPage(IReadOnlyList<AllVersionsEntry> Entries, bool IsTruncated);
internal sealed record CurrentPage(IReadOnlyList<VersionListEntry> Entries, bool IsTruncated);

internal sealed record BucketCatalogEntry(
    string Name,
    long CreatedAtUnixMs,
    long DeletedAtUnixMs,
    long AppliedSeq,
    long FirstAvailableLogSeq,
    BucketConfigSnapshot? Config = null);

internal interface IClusterBucketSyncer
{
    bool TrySyncBucketCatalog(string bucket);
    bool TrySyncBucketEvents(string bucket);
}

internal interface IBucketRegistry : IDisposable, IBlobReferenceSource
{
    string IBlobReferenceSource.ProtocolName => "Objects";

    bool IsValidName(string bucket);

    Result<bool> Create(string bucket, string? ownerId = null);
    Result<bool> Create(string bucket, CallerIdentity caller, string? explicitOwnerId = null) =>
        !caller.CanWrite
            ? new AccessDeniedError("ReadOnly users cannot create buckets")
            : Create(bucket, caller.IsAdmin ? (explicitOwnerId ?? caller.UserId) : caller.UserId);

    Result Delete(string bucket);
    Result Delete(string bucket, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : Delete(bucket);

    Result<bool> Exists(string bucket);
    Result EnsureExists(string bucket);
    IEnumerable<BucketInfo> List(string? ownerId = null);
    IEnumerable<BucketInfo> List(CallerIdentity caller) =>
        List(caller.IsAdmin ? null : caller.UserId);

    Result<string?> GetOwner(string bucket);
    Result<string?> GetOwner(string bucket, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure f ? f.Error : GetOwner(bucket);

    Result SetOwner(string bucket, string newOwnerId);
    Result SetOwner(string bucket, string newOwnerId, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : SetOwner(bucket, newOwnerId);

    Result AuthorizeAccess(string bucket, CallerIdentity? caller, BucketCapability capability) =>
        Result.Ok;

    Result<PutEntry> GetCurrentPut(string bucket, string key);
    Result<PutEntry> GetVersion(string bucket, string key, string versionId);
    Result<PutEntry> AppendPut(string bucket, string key, PutRequest req);
    Result<DeleteOutcome> AppendDelete(string bucket, string key, bool bypassGovernance);
    Result<DeleteOutcome> HardDeleteVersion(string bucket, string key, string versionId, bool bypassGovernance);
    Result<IReadOnlyList<Result<DeleteOutcome>>> DeleteBatch(string bucket, IReadOnlyList<BatchDeleteItem> items);
    Result<CurrentPage> ListCurrent(string bucket, string? prefix, KeyBound? from, int limit);
    Result<VersionsPage> ListAllVersions(string bucket, string? prefix, string? keyMarker, int limit);
    Result<VersioningStatus> GetVersioning(string bucket);
    Result SetVersioning(string bucket, VersioningStatus status);
    Result SetVersioning(string bucket, VersioningStatus status, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : SetVersioning(bucket, status);

    Result<PutTaggingOutcome> PutTagging(string bucket, string key, string? versionId, IReadOnlyDictionary<string, string> tags);
    VersionKind? GetCurrentKind(string bucket, string key);
    VersionKind? GetVersionKind(string bucket, string key, string versionId);
    Result<ObjectLockConfig> GetObjectLock(string bucket);
    Result SetObjectLock(string bucket, ObjectLockConfig cfg);
    Result SetObjectLock(string bucket, ObjectLockConfig cfg, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : SetObjectLock(bucket, cfg);

    Result<LifecycleConfig> GetLifecycle(string bucket);
    Result SetLifecycle(string bucket, LifecycleConfig cfg);
    Result SetLifecycle(string bucket, LifecycleConfig cfg, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : SetLifecycle(bucket, cfg);

    Result RemoveLifecycle(string bucket);
    Result RemoveLifecycle(string bucket, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : RemoveLifecycle(bucket);

    Result<WebsiteConfig> GetWebsite(string bucket);
    Result SetWebsite(string bucket, WebsiteConfig cfg);
    Result SetWebsite(string bucket, WebsiteConfig cfg, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : SetWebsite(bucket, cfg);

    Result RemoveWebsite(string bucket);
    Result RemoveWebsite(string bucket, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : RemoveWebsite(bucket);

    Result<BucketAccess> GetAccess(string bucket);
    Result SetAccess(string bucket, BucketAccess access);
    Result SetAccess(string bucket, BucketAccess access, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : SetAccess(bucket, access);

    Result<CorsConfig> GetCors(string bucket);
    Result SetCors(string bucket, CorsConfig cfg);
    Result SetCors(string bucket, CorsConfig cfg, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : SetCors(bucket, cfg);

    Result RemoveCors(string bucket);
    Result RemoveCors(string bucket, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : RemoveCors(bucket);

    IEnumerable<Bucket> OpenBuckets();
    Bucket? GetOpenBucket(string bucket) => null;
    IReadOnlyList<BucketCatalogEntry> GetCatalogEntries() => [];
    bool ReconcileCatalogEntry(BucketCatalogEntry entry) => false;
    void RegisterClusterSyncer(IClusterBucketSyncer syncer) { }
    Result PutRetention(string bucket, string key, string versionId, Retention retention, bool bypassGovernance);
    Result<Retention?> GetRetention(string bucket, string key, string versionId);
    Result PutLegalHold(string bucket, string key, string versionId, bool on);
    Result<bool> GetLegalHold(string bucket, string key, string versionId);
}

internal sealed class BucketRegistry(BucketRegistryOptions options, IFileSync fileSync, IDurableWrite durableWrite, IWebhookEventPublisher? publisher = null) : IBucketRegistry
{
    private const string BucketsDirName = "buckets";
    private const string TombstonesFileName = ".tombstones.txt";

    private static readonly AsyncLocal<bool> SyncInProgress = new();

    private readonly string bucketsRoot = Path.Combine(options.Root, BucketsDirName);
    private readonly string tombstonesPath = Path.Combine(options.Root, BucketsDirName, TombstonesFileName);
    private readonly ConcurrentDictionary<string, Lazy<Bucket>> openBuckets = new();
    private readonly ConcurrentDictionary<string, long> deletedBuckets = LoadTombstones(Path.Combine(options.Root, BucketsDirName, TombstonesFileName));
    private readonly Lock createDeleteGate = new();
    private IClusterBucketSyncer? clusterSync;

    public void RegisterClusterSyncer(IClusterBucketSyncer syncer) => clusterSync = syncer;

    private static readonly SearchValues<char> ValidBucketChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-.");

    public bool IsValidName(string bucket) =>
        !string.IsNullOrEmpty(bucket)
        && bucket.Length is >= 3 and <= 63
        && bucket[0] is not ('-' or '.')
        && bucket[^1] is not ('-' or '.')
        && !bucket.Contains("..", StringComparison.Ordinal)
        && !bucket.ContainsAnyExcept(ValidBucketChars);

    public Result<bool> Create(string bucket, string? ownerId = null)
    {
        if (!IsValidName(bucket)) return new InvalidBucketNameError(bucket);

        var path = Path.Combine(bucketsRoot, bucket);
        lock (createDeleteGate)
        {
            if (Directory.Exists(path)) return false;
            if (fileSync.CreateDirectoryDurable(path) is Result.Failure failure) return failure.Error;
            var targetBucket = OpenLocked(bucket, path);
            if (ownerId is not null && targetBucket is not null)
                targetBucket.SetOwner(ownerId);
            if (deletedBuckets.TryRemove(bucket, out _))
                PersistTombstonesLocked();
        }
        publisher?.Publish(VesselEvents.BucketCreated(bucket, ownerId));
        return true;
    }

    public Result<bool> Create(string bucket, CallerIdentity caller, string? explicitOwnerId = null)
    {
        if (!caller.CanWrite)
            return new AccessDeniedError("ReadOnly users cannot create buckets");

        var ownerId = caller.IsAdmin ? (explicitOwnerId ?? caller.UserId) : caller.UserId;
        return Create(bucket, ownerId);
    }

    public Result Delete(string bucket)
    {
        if (!IsValidName(bucket)) return new InvalidBucketNameError(bucket);

        var path = Path.Combine(bucketsRoot, bucket);
        lock (createDeleteGate)
        {
            if (!Directory.Exists(path)) return new NoSuchBucketError(bucket);

            var targetBucket = OpenLocked(bucket, path);
            if (targetBucket is not null && !targetBucket.TrySealForDelete()) return new BucketNotEmptyError(bucket);

            if (openBuckets.TryRemove(bucket, out var lazyBucket) && lazyBucket.IsValueCreated)
                lazyBucket.Value.Dispose();

            Directory.Delete(path, recursive: true);
            deletedBuckets[bucket] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PersistTombstonesLocked();
        }
        publisher?.Publish(VesselEvents.BucketDeleted(bucket));
        return Result.Ok;
    }

    public Result Delete(string bucket, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure failure
            ? failure.Error
            : Delete(bucket);

    public Result<bool> Exists(string bucket)
    {
        if (!IsValidName(bucket)) return new InvalidBucketNameError(bucket);
        var path = Path.Combine(bucketsRoot, bucket);
        if (Directory.Exists(path)) return true;
        if (TrySyncCatalogFromPeers(bucket) && Directory.Exists(path)) return true;
        return false;
    }

    public Result EnsureExists(string bucket)
    {
        if (!IsValidName(bucket)) return new InvalidBucketNameError(bucket);
        var path = Path.Combine(bucketsRoot, bucket);
        if (Directory.Exists(path) || (TrySyncCatalogFromPeers(bucket) && Directory.Exists(path)))
            return Result.Ok;
        return new NoSuchBucketError(bucket);
    }

    public IEnumerable<BucketInfo> List(string? ownerId = null)
    {
        if (!Directory.Exists(bucketsRoot)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(bucketsRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            var bucket = Open(name);
            var bucketOwner = bucket?.GetOwner();
            if (ownerId is not null && !string.Equals(bucketOwner, ownerId, StringComparison.Ordinal))
                continue;
            yield return new BucketInfo(name, bucket?.CreatedAt ?? Directory.GetCreationTimeUtc(dir), bucketOwner);
        }
    }

    public IEnumerable<BucketInfo> List(CallerIdentity caller) =>
        List(caller.IsAdmin ? null : caller.UserId);

    public Result<string?> GetOwner(string bucket) =>
        WithBucketValue(bucket, b => b.GetOwner());

    public Result<string?> GetOwner(string bucket, CallerIdentity caller) =>
        WithBucket(bucket, b =>
            BucketPolicy.Authorize(caller, b, BucketCapability.Read) is Result.Failure f
                ? f.Error
                : (Result<string?>)b.GetOwner());

    public Result SetOwner(string bucket, string newOwnerId) =>
        string.IsNullOrWhiteSpace(newOwnerId)
            ? new InvalidArgumentError("OwnerId cannot be empty.")
            : WithBucket(bucket, b =>
            {
                b.SetOwner(newOwnerId);
                return Result.Ok;
            });

    public Result SetOwner(string bucket, string newOwnerId, CallerIdentity caller) =>
        string.IsNullOrWhiteSpace(newOwnerId)
            ? new InvalidArgumentError("OwnerId cannot be empty.")
            : WithBucket(bucket, b =>
                BucketPolicy.Authorize(caller, b, BucketCapability.Admin) is Result.Failure f
                    ? f.Error
                    : SetOwner(bucket, newOwnerId));

    public Result AuthorizeAccess(string bucket, CallerIdentity? caller, BucketCapability capability) =>
        WithBucket(bucket, b => BucketPolicy.Authorize(caller, b, capability));

    public Result<PutEntry> GetCurrentPut(string bucket, string key) =>
        WithKey(bucket, key, b =>
        {
            var res = b.GetCurrentPut(key);
            if (res is Result<PutEntry>.Failure { Error: NoSuchKeyError } && TrySyncEventsFromPeers(bucket))
                return b.GetCurrentPut(key);
            return res;
        });

    public Result<PutEntry> GetVersion(string bucket, string key, string versionId) =>
        WithKey(bucket, key, b =>
        {
            var res = b.GetVersion(key, versionId);
            if (res is Result<PutEntry>.Failure { Error: NoSuchKeyError or NoSuchVersionError } && TrySyncEventsFromPeers(bucket))
                return b.GetVersion(key, versionId);
            return res;
        });

    public Result<PutEntry> AppendPut(string bucket, string key, PutRequest req) =>
        WithKey<PutEntry>(bucket, key, b => b.AppendPut(key, req));

    public Result<DeleteOutcome> AppendDelete(string bucket, string key, bool bypassGovernance) =>
        WithKey<DeleteOutcome>(bucket, key, b => b.AppendDelete(key, bypassGovernance));

    public Result<DeleteOutcome> HardDeleteVersion(string bucket, string key, string versionId, bool bypassGovernance) =>
        WithKey<DeleteOutcome>(bucket, key, b => b.HardDeleteVersion(key, versionId, bypassGovernance));

    public Result<IReadOnlyList<Result<DeleteOutcome>>> DeleteBatch(string bucket, IReadOnlyList<BatchDeleteItem> items) =>
        WithBucket<IReadOnlyList<Result<DeleteOutcome>>>(bucket,
            b => new Result<IReadOnlyList<Result<DeleteOutcome>>>.Success(b.AppendDeleteBatch(items)));

    public Result<ObjectLockConfig> GetObjectLock(string bucket) =>
        WithBucket<ObjectLockConfig>(bucket, b => b.ObjectLock is { } cfg
            ? cfg
            : new ObjectLockConfigurationNotFoundErrorResult(bucket));

    public Result SetObjectLock(string bucket, ObjectLockConfig cfg) =>
        WithBucket(bucket, b => b.SetObjectLock(cfg));

    public Result SetObjectLock(string bucket, ObjectLockConfig cfg, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : SetObjectLock(bucket, cfg);

    public Result<LifecycleConfig> GetLifecycle(string bucket) =>
        WithBucket<LifecycleConfig>(bucket, b => b.Lifecycle is { } cfg
            ? cfg
            : new NoSuchLifecycleConfigurationError(bucket));

    public Result SetLifecycle(string bucket, LifecycleConfig cfg) =>
        WithBucket(bucket, b => b.SetLifecycle(cfg));

    public Result SetLifecycle(string bucket, LifecycleConfig cfg, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : SetLifecycle(bucket, cfg);

    public Result RemoveLifecycle(string bucket) =>
        WithBucket(bucket, b => b.RemoveLifecycle());

    public Result RemoveLifecycle(string bucket, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : RemoveLifecycle(bucket);

    public Result<WebsiteConfig> GetWebsite(string bucket) =>
        WithBucket<WebsiteConfig>(bucket, b => b.Website is { } cfg
            ? cfg
            : new NoSuchWebsiteConfigurationError(bucket));

    public Result SetWebsite(string bucket, WebsiteConfig cfg) =>
        WithBucket(bucket, b => b.SetWebsite(cfg));

    public Result SetWebsite(string bucket, WebsiteConfig cfg, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : SetWebsite(bucket, cfg);

    public Result RemoveWebsite(string bucket) =>
        WithBucket(bucket, b => b.RemoveWebsite());

    public Result RemoveWebsite(string bucket, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : RemoveWebsite(bucket);

    public Result<BucketAccess> GetAccess(string bucket) =>
        WithBucketValue(bucket, b => b.Access);

    public Result SetAccess(string bucket, BucketAccess access) =>
        WithBucket(bucket, b => b.SetAccess(access));

    public Result SetAccess(string bucket, BucketAccess access, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : SetAccess(bucket, access);

    public Result<CorsConfig> GetCors(string bucket) =>
        WithBucket<CorsConfig>(bucket, b => b.Cors is { } cfg
            ? cfg
            : new NoSuchCORSConfigurationError(bucket));

    public Result SetCors(string bucket, CorsConfig cfg) =>
        WithBucket(bucket, b => b.SetCors(cfg));

    public Result SetCors(string bucket, CorsConfig cfg, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : SetCors(bucket, cfg);

    public Result RemoveCors(string bucket) =>
        WithBucket(bucket, b => b.RemoveCors());

    public Result RemoveCors(string bucket, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : RemoveCors(bucket);

    public IEnumerable<Bucket> OpenBuckets()
    {
        foreach (var info in List())
            if (Open(info.Name) is { } b) yield return b;
    }

    public Bucket? GetOpenBucket(string bucket) =>
        IsValidName(bucket) && Directory.Exists(Path.Combine(bucketsRoot, bucket))
            ? Open(bucket)
            : null;

    public IReadOnlyList<BucketCatalogEntry> GetCatalogEntries()
    {
        var entries = new List<BucketCatalogEntry>();
        foreach (var b in OpenBuckets())
        {
            entries.Add(new BucketCatalogEntry(
                b.Name,
                b.CreatedAt.ToUnixTimeMilliseconds(),
                0,
                b.AppliedSeq,
                b.FirstAvailableLogSeq,
                b.GetConfigSnapshot()));
        }

        foreach (var kvp in deletedBuckets)
        {
            if (entries.Exists(e => string.Equals(e.Name, kvp.Key, StringComparison.Ordinal)))
                continue;
            entries.Add(new BucketCatalogEntry(kvp.Key, 0, kvp.Value, 0, 1, null));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return entries;
    }

    public bool ReconcileCatalogEntry(BucketCatalogEntry entry)
    {
        if (!IsValidName(entry.Name)) return false;

        var path = Path.Combine(bucketsRoot, entry.Name);
        lock (createDeleteGate)
        {
            return entry.DeletedAtUnixMs > 0
                ? ReconcileDeletedBucketLocked(entry, path)
                : ReconcileActiveBucketLocked(entry, path);
        }
    }

    private bool ReconcileDeletedBucketLocked(BucketCatalogEntry entry, string path)
    {
        if (!Directory.Exists(path))
        {
            if (!deletedBuckets.TryGetValue(entry.Name, out var prevDel) || entry.DeletedAtUnixMs > prevDel)
            {
                deletedBuckets[entry.Name] = entry.DeletedAtUnixMs;
                PersistTombstonesLocked();
            }
            return false;
        }

        var existing = OpenLocked(entry.Name, path);
        var createdMs = existing?.CreatedAt.ToUnixTimeMilliseconds() ?? 0;
        if (entry.DeletedAtUnixMs < createdMs || existing is null || !existing.TrySealForDelete())
            return Directory.Exists(path);

        if (openBuckets.TryRemove(entry.Name, out var lazyBucket) && lazyBucket.IsValueCreated)
            lazyBucket.Value.Dispose();
        Directory.Delete(path, recursive: true);
        deletedBuckets[entry.Name] = entry.DeletedAtUnixMs;
        PersistTombstonesLocked();
        return false;
    }

    private bool ReconcileActiveBucketLocked(BucketCatalogEntry entry, string path)
    {
        if (Directory.Exists(path))
        {
            var bucket = OpenLocked(entry.Name, path);
            if (bucket is not null && entry.Config is not null)
                bucket.ApplyConfigSnapshot(entry.Config);
            return true;
        }

        if (deletedBuckets.TryGetValue(entry.Name, out var localDel) && localDel > entry.CreatedAtUnixMs)
            return false;

        if (fileSync.CreateDirectoryDurable(path) is Result.Failure)
            return false;

        var created = OpenLocked(entry.Name, path);
        if (created is not null)
        {
            if (entry.CreatedAtUnixMs > 0)
                created.SetCreatedAtUnixMs(entry.CreatedAtUnixMs);
            if (entry.Config is not null)
                created.ApplyConfigSnapshot(entry.Config, force: true);
        }
        if (deletedBuckets.TryRemove(entry.Name, out _))
            PersistTombstonesLocked();
        return true;
    }

    public Result PutRetention(string bucket, string key, string versionId, Retention retention, bool bypassGovernance) =>
        WithKey(bucket, key, b => b.PutRetention(key, versionId, retention, bypassGovernance));

    public Result<Retention?> GetRetention(string bucket, string key, string versionId) =>
        WithKey<Retention?>(bucket, key, b =>
        {
            var (r, _) = b.GetObjectLockStatus(key, versionId);
            return r;
        });

    public Result PutLegalHold(string bucket, string key, string versionId, bool on) =>
        WithKey(bucket, key, b => b.PutLegalHold(key, versionId, on));

    public Result<bool> GetLegalHold(string bucket, string key, string versionId) =>
        WithKey<bool>(bucket, key, b =>
        {
            var (_, h) = b.GetObjectLockStatus(key, versionId);
            return h;
        });

    public Result<CurrentPage> ListCurrent(string bucket, string? prefix, KeyBound? from, int limit) =>
        WithBucketValue(bucket, b =>
        {
            var (entries, truncated) = b.ListCurrent(prefix, from, limit);
            return new CurrentPage(entries, truncated);
        });

    private Result<T> WithBucketValue<T>(string bucket, Func<Bucket, T> body)
    {
        if (!IsValidName(bucket)) return new InvalidBucketNameError(bucket);
        if (Open(bucket) is not { } b) return new NoSuchBucketError(bucket);
        return body(b);
    }

    private Result<T> WithBucket<T>(string bucket, Func<Bucket, Result<T>> body)
    {
        if (!IsValidName(bucket)) return new InvalidBucketNameError(bucket);
        if (Open(bucket) is not { } b) return new NoSuchBucketError(bucket);
        return body(b);
    }

    private Result WithBucket(string bucket, Func<Bucket, Result> body)
    {
        if (!IsValidName(bucket)) return new InvalidBucketNameError(bucket);
        if (Open(bucket) is not { } b) return new NoSuchBucketError(bucket);
        return body(b);
    }

    private Result<T> WithKey<T>(string bucket, string key, Func<Bucket, Result<T>> body)
    {
        if (!IsValidName(bucket)) return new InvalidBucketNameError(bucket);
        if (string.IsNullOrEmpty(key)) return new InvalidPathError($"{bucket}/{key}");
        if (Open(bucket) is not { } b) return new NoSuchBucketError(bucket);
        return body(b);
    }

    private Result WithKey(string bucket, string key, Func<Bucket, Result> body)
    {
        if (!IsValidName(bucket)) return new InvalidBucketNameError(bucket);
        if (string.IsNullOrEmpty(key)) return new InvalidPathError($"{bucket}/{key}");
        if (Open(bucket) is not { } b) return new NoSuchBucketError(bucket);
        return body(b);
    }

    public Result<VersionsPage> ListAllVersions(string bucket, string? prefix, string? keyMarker, int limit) =>
        WithBucketValue(bucket, b =>
        {
            var (entries, truncated) = b.ListAllVersions(prefix, keyMarker, limit);
            return new VersionsPage(entries, truncated);
        });

    public Result<VersioningStatus> GetVersioning(string bucket) =>
        WithBucketValue(bucket, b => b.Versioning);

    public Result<PutTaggingOutcome> PutTagging(string bucket, string key, string? versionId, IReadOnlyDictionary<string, string> tags) =>
        WithKey<PutTaggingOutcome>(bucket, key, targetBucket =>
        {
            var resolveResult = ResolveTargetVersionId(targetBucket, key, versionId);
            return !resolveResult.TryGetValue(out var targetVersionId, out var error)
                ? error
                : targetBucket.AppendPutTagging(key, targetVersionId, tags);
        });

    private static Result<string> ResolveTargetVersionId(Bucket bucket, string key, string? versionId)
    {
        if (versionId is null)
        {
            return bucket.GetCurrentPut(key).TryGetValue(out var currentPut, out var currentError)
                ? currentPut.VersionId
                : currentError;
        }

        return bucket.GetVersion(key, versionId).TryGetValue(out var specificVersion, out var versionError)
            ? specificVersion.VersionId
            : versionError;
    }

    public VersionKind? GetCurrentKind(string bucket, string key) =>
        !IsValidName(bucket) || string.IsNullOrEmpty(key) ? null : Open(bucket)?.Index.GetCurrentKind(key);

    public VersionKind? GetVersionKind(string bucket, string key, string versionId) =>
        !IsValidName(bucket) || string.IsNullOrEmpty(key) ? null : Open(bucket)?.Index.GetVersionKind(key, versionId);

    public Result SetVersioning(string bucket, VersioningStatus status) =>
        WithBucket(bucket, b => b.SetVersioning(status));

    public Result SetVersioning(string bucket, VersioningStatus status, CallerIdentity caller) =>
        AuthorizeAccess(bucket, caller, BucketCapability.Admin) is Result.Failure f ? f.Error : SetVersioning(bucket, status);

    public async IAsyncEnumerable<string> AllReferencedBlobs([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var info in List())
        {
            var b = Open(info.Name);
            if (b is null) continue;
            await foreach (var sha in b.Index.ReferencedBlobs(ct).WithCancellation(ct))
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
        if (!Directory.Exists(path) && (!TrySyncCatalogFromPeers(bucket) || !Directory.Exists(path)))
            return null;

        lock (createDeleteGate)
            return OpenLocked(bucket, path);
    }

    private bool TrySyncCatalogFromPeers(string bucket)
    {
        if (clusterSync is null || SyncInProgress.Value)
            return false;
        SyncInProgress.Value = true;
        try
        {
            return clusterSync.TrySyncBucketCatalog(bucket);
        }
        finally
        {
            SyncInProgress.Value = false;
        }
    }

    private bool TrySyncEventsFromPeers(string bucket)
    {
        if (clusterSync is null || SyncInProgress.Value)
            return false;
        SyncInProgress.Value = true;
        try
        {
            return clusterSync.TrySyncBucketEvents(bucket);
        }
        finally
        {
            SyncInProgress.Value = false;
        }
    }

    private Bucket? OpenLocked(string bucket, string path)
    {
        if (!Directory.Exists(path)) return null;

        var lazy = openBuckets.GetOrAdd(bucket, _ => new Lazy<Bucket>(() =>
        {
            var b = new Bucket(bucket, path, fileSync, durableWrite, localNodeId: options.NodeId);
            b.Open();
            return b;
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        return ResolveEvictingOnFault(bucket, lazy);
    }

    private void PersistTombstonesLocked()
    {
        Directory.CreateDirectory(bucketsRoot);
        var sb = new StringBuilder();
        foreach (var kvp in deletedBuckets.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(kvp.Key).Append('\t').Append(kvp.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        _ = durableWrite.AtomicReplace(tombstonesPath, sb.ToString());
    }

    private static ConcurrentDictionary<string, long> LoadTombstones(string filePath)
    {
        var dict = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        if (!File.Exists(filePath)) return dict;
        foreach (var line in File.ReadAllLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var name = line[..tab];
            if (long.TryParse(line.AsSpan(tab + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
                dict[name] = ts;
        }
        return dict;
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
