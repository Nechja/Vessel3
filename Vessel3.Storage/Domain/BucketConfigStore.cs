using System.Globalization;
using System.Text.Json;

namespace Vessel3.Storage;

internal sealed record BucketConfigSnapshot(
    long UpdatedAtUnixMs,
    string? OwnerId = null,
    VersioningStatus Versioning = VersioningStatus.Unversioned,
    ObjectLockConfig? ObjectLock = null,
    LifecycleConfig? Lifecycle = null,
    WebsiteConfig? Website = null,
    BucketAccess? Access = null,
    CorsConfig? Cors = null);

internal sealed class BucketConfigStore(string path, IDurableWrite durableWrite)
{
    private const string VersioningFileName = "versioning.txt";
    private const string ObjectLockFileName = "object-lock.json";
    private const string LifecycleFileName = "lifecycle.json";
    private const string WebsiteFileName = "website.json";
    private const string AccessFileName = "access.json";
    private const string CorsFileName = "cors.json";
    private const string UpdatedAtFileName = "config-updated-at.txt";

    private readonly string versioningPath = Path.Combine(path, VersioningFileName);
    private readonly string objectLockPath = Path.Combine(path, ObjectLockFileName);
    private readonly string lifecyclePath = Path.Combine(path, LifecycleFileName);
    private readonly string websitePath = Path.Combine(path, WebsiteFileName);
    private readonly string accessPath = Path.Combine(path, AccessFileName);
    private readonly string corsPath = Path.Combine(path, CorsFileName);
    private readonly string updatedAtPath = Path.Combine(path, UpdatedAtFileName);

    public VersioningStatus Versioning { get; private set; }
    public ObjectLockConfig? ObjectLock { get; private set; }
    public LifecycleConfig? Lifecycle { get; private set; }
    public WebsiteConfig? Website { get; private set; }
    public BucketAccess Access { get; private set; } = BucketAccess.Private;
    public CorsConfig? Cors { get; private set; }
    public long UpdatedAtUnixMs { get; private set; }

    public void Load()
    {
        Versioning = ReadVersioning();
        ObjectLock = ReadObjectLock();
        Lifecycle = ReadLifecycle();
        Website = ReadWebsite();
        Access = ReadAccess();
        Cors = ReadCors();
        UpdatedAtUnixMs = ReadUpdatedAt();
    }

    public BucketConfigSnapshot ToSnapshot(string? ownerId) =>
        new(UpdatedAtUnixMs, ownerId, Versioning, ObjectLock, Lifecycle, Website, Access, Cors);

    public void TouchUpdatedAt()
    {
        var next = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), UpdatedAtUnixMs + 1);
        UpdatedAtUnixMs = next;
        _ = durableWrite.AtomicReplace(updatedAtPath, next.ToString(CultureInfo.InvariantCulture));
    }

    public bool ApplySnapshot(BucketConfigSnapshot snapshot, bool force = false)
    {
        if (!force && snapshot.UpdatedAtUnixMs <= UpdatedAtUnixMs)
            return false;

        Versioning = snapshot.Versioning;
        if (snapshot.Versioning is VersioningStatus.Unversioned)
        {
            if (File.Exists(versioningPath)) File.Delete(versioningPath);
        }
        else
        {
            _ = durableWrite.AtomicReplace(versioningPath, snapshot.Versioning.ToString());
        }

        ObjectLock = snapshot.ObjectLock;
        if (snapshot.ObjectLock is null)
        {
            if (File.Exists(objectLockPath)) File.Delete(objectLockPath);
        }
        else
        {
            _ = durableWrite.AtomicReplace(objectLockPath, JsonSerializer.Serialize(snapshot.ObjectLock, ObjectLockJsonContext.Default.ObjectLockConfig));
        }

        Lifecycle = snapshot.Lifecycle;
        if (snapshot.Lifecycle is null)
        {
            if (File.Exists(lifecyclePath)) File.Delete(lifecyclePath);
        }
        else
        {
            _ = durableWrite.AtomicReplace(lifecyclePath, JsonSerializer.Serialize(snapshot.Lifecycle, LifecycleJsonContext.Default.LifecycleConfig));
        }

        Website = snapshot.Website;
        if (snapshot.Website is null)
        {
            if (File.Exists(websitePath)) File.Delete(websitePath);
        }
        else
        {
            _ = durableWrite.AtomicReplace(websitePath, JsonSerializer.Serialize(snapshot.Website, WebsiteJsonContext.Default.WebsiteConfig));
        }

        var effectiveAccess = snapshot.Access ?? BucketAccess.Private;
        Access = effectiveAccess;
        _ = durableWrite.AtomicReplace(accessPath, JsonSerializer.Serialize(effectiveAccess, BucketAccessJsonContext.Default.BucketAccess));

        Cors = snapshot.Cors;
        if (snapshot.Cors is null)
        {
            if (File.Exists(corsPath)) File.Delete(corsPath);
        }
        else
        {
            _ = durableWrite.AtomicReplace(corsPath, JsonSerializer.Serialize(snapshot.Cors, CorsJsonContext.Default.CorsConfig));
        }

        UpdatedAtUnixMs = snapshot.UpdatedAtUnixMs;
        if (snapshot.UpdatedAtUnixMs > 0)
            _ = durableWrite.AtomicReplace(updatedAtPath, snapshot.UpdatedAtUnixMs.ToString(CultureInfo.InvariantCulture));

        return true;
    }

    public Result SetVersioning(VersioningStatus status)
    {
        if (ObjectLock is { Enabled: true }
            && status is VersioningStatus.Suspended or VersioningStatus.Unversioned)
            return new InvalidBucketStateError("cannot suspend versioning on a bucket with Object Lock enabled");

        if (status is VersioningStatus.Unversioned)
        {
            if (File.Exists(versioningPath)) File.Delete(versioningPath);
            Versioning = status;
            TouchUpdatedAt();
            return Result.Ok;
        }
        var res = durableWrite.AtomicReplace(versioningPath, status.ToString());
        if (res is not Result.Failure)
        {
            Versioning = status;
            TouchUpdatedAt();
        }
        return res;
    }

    public Result SetObjectLock(ObjectLockConfig cfg)
    {
        if (cfg.Enabled && Versioning is not VersioningStatus.Enabled)
            return new InvalidBucketStateError("Object Lock requires versioning to be Enabled");
        if (ObjectLock is { Enabled: true } && !cfg.Enabled)
            return new InvalidBucketStateError("Object Lock cannot be disabled once enabled");

        var res = durableWrite.AtomicReplace(objectLockPath, JsonSerializer.Serialize(cfg, ObjectLockJsonContext.Default.ObjectLockConfig));
        if (res is not Result.Failure)
        {
            ObjectLock = cfg;
            TouchUpdatedAt();
        }
        return res;
    }

    public Result SetLifecycle(LifecycleConfig cfg)
    {
        var res = durableWrite.AtomicReplace(lifecyclePath, JsonSerializer.Serialize(cfg, LifecycleJsonContext.Default.LifecycleConfig));
        if (res is not Result.Failure)
        {
            Lifecycle = cfg;
            TouchUpdatedAt();
        }
        return res;
    }

    public Result RemoveLifecycle()
    {
        if (File.Exists(lifecyclePath)) File.Delete(lifecyclePath);
        Lifecycle = null;
        TouchUpdatedAt();
        return Result.Ok;
    }

    public Result SetWebsite(WebsiteConfig cfg)
    {
        var res = durableWrite.AtomicReplace(websitePath, JsonSerializer.Serialize(cfg, WebsiteJsonContext.Default.WebsiteConfig));
        if (res is not Result.Failure)
        {
            Website = cfg;
            TouchUpdatedAt();
        }
        return res;
    }

    public Result RemoveWebsite()
    {
        if (File.Exists(websitePath)) File.Delete(websitePath);
        Website = null;
        TouchUpdatedAt();
        return Result.Ok;
    }

    public Result SetAccess(BucketAccess access)
    {
        var res = durableWrite.AtomicReplace(accessPath, JsonSerializer.Serialize(access, BucketAccessJsonContext.Default.BucketAccess));
        if (res is not Result.Failure)
        {
            Access = access;
            TouchUpdatedAt();
        }
        return res;
    }

    public Result SetCors(CorsConfig cfg)
    {
        var res = durableWrite.AtomicReplace(corsPath, JsonSerializer.Serialize(cfg, CorsJsonContext.Default.CorsConfig));
        if (res is not Result.Failure)
        {
            Cors = cfg;
            TouchUpdatedAt();
        }
        return res;
    }

    public Result RemoveCors()
    {
        if (File.Exists(corsPath)) File.Delete(corsPath);
        Cors = null;
        TouchUpdatedAt();
        return Result.Ok;
    }

    private long ReadUpdatedAt() =>
        File.Exists(updatedAtPath)
            && long.TryParse(File.ReadAllText(updatedAtPath).AsSpan().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts)
                ? ts : 0;

    private VersioningStatus ReadVersioning() =>
        File.Exists(versioningPath)
            && Enum.TryParse<VersioningStatus>(File.ReadAllText(versioningPath).AsSpan().Trim(), out var s)
                ? s : VersioningStatus.Unversioned;

    private ObjectLockConfig? ReadObjectLock() =>
        File.Exists(objectLockPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(objectLockPath), ObjectLockJsonContext.Default.ObjectLockConfig)
            : null;

    private LifecycleConfig? ReadLifecycle() =>
        File.Exists(lifecyclePath)
            ? JsonSerializer.Deserialize(File.ReadAllText(lifecyclePath), LifecycleJsonContext.Default.LifecycleConfig)
            : null;

    private WebsiteConfig? ReadWebsite() =>
        File.Exists(websitePath)
            ? JsonSerializer.Deserialize(File.ReadAllText(websitePath), WebsiteJsonContext.Default.WebsiteConfig)
            : null;

    private BucketAccess ReadAccess() =>
        File.Exists(accessPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(accessPath), BucketAccessJsonContext.Default.BucketAccess) ?? BucketAccess.Private
            : BucketAccess.Private;

    private CorsConfig? ReadCors() =>
        File.Exists(corsPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(corsPath), CorsJsonContext.Default.CorsConfig)
            : null;
}
