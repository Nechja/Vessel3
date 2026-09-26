using System.Text.Json;

namespace Vessel3.Storage;

internal sealed class BucketConfigStore(string path, IDurableWrite durableWrite)
{
    private readonly string versioningPath = Path.Combine(path, "versioning.txt");
    private readonly string objectLockPath = Path.Combine(path, "object-lock.json");
    private readonly string lifecyclePath = Path.Combine(path, "lifecycle.json");
    private readonly string websitePath = Path.Combine(path, "website.json");
    private readonly string accessPath = Path.Combine(path, "access.json");
    private readonly string corsPath = Path.Combine(path, "cors.json");

    public VersioningStatus Versioning { get; private set; }
    public ObjectLockConfig? ObjectLock { get; private set; }
    public LifecycleConfig? Lifecycle { get; private set; }
    public WebsiteConfig? Website { get; private set; }
    public BucketAccess Access { get; private set; } = BucketAccess.Private;
    public CorsConfig? Cors { get; private set; }

    public void Load()
    {
        Versioning = ReadVersioning();
        ObjectLock = ReadObjectLock();
        Lifecycle = ReadLifecycle();
        Website = ReadWebsite();
        Access = ReadAccess();
        Cors = ReadCors();
    }

    public Result SetVersioning(VersioningStatus status)
    {
        if (ObjectLock is { Enabled: true }
            && status is VersioningStatus.Suspended or VersioningStatus.Unversioned)
            return new InvalidBucketStateError("cannot suspend versioning on a bucket with Object Lock enabled");

        Versioning = status;
        if (status is VersioningStatus.Unversioned)
        {
            if (File.Exists(versioningPath)) File.Delete(versioningPath);
            return Result.Ok;
        }
        return durableWrite.AtomicReplace(versioningPath, status.ToString());
    }

    public Result SetObjectLock(ObjectLockConfig cfg)
    {
        if (cfg.Enabled && Versioning is not VersioningStatus.Enabled)
            return new InvalidBucketStateError("Object Lock requires versioning to be Enabled");
        if (ObjectLock is { Enabled: true } && !cfg.Enabled)
            return new InvalidBucketStateError("Object Lock cannot be disabled once enabled");

        ObjectLock = cfg;
        return durableWrite.AtomicReplace(objectLockPath, JsonSerializer.Serialize(cfg, ObjectLockJsonContext.Default.ObjectLockConfig));
    }

    public Result SetLifecycle(LifecycleConfig cfg)
    {
        Lifecycle = cfg;
        return durableWrite.AtomicReplace(lifecyclePath, JsonSerializer.Serialize(cfg, LifecycleJsonContext.Default.LifecycleConfig));
    }

    public Result RemoveLifecycle()
    {
        Lifecycle = null;
        if (File.Exists(lifecyclePath)) File.Delete(lifecyclePath);
        return Result.Ok;
    }

    public Result SetWebsite(WebsiteConfig cfg)
    {
        Website = cfg;
        return durableWrite.AtomicReplace(websitePath, JsonSerializer.Serialize(cfg, WebsiteJsonContext.Default.WebsiteConfig));
    }

    public Result RemoveWebsite()
    {
        Website = null;
        if (File.Exists(websitePath)) File.Delete(websitePath);
        return Result.Ok;
    }

    public Result SetAccess(BucketAccess access)
    {
        Access = access;
        return durableWrite.AtomicReplace(accessPath, JsonSerializer.Serialize(access, BucketAccessJsonContext.Default.BucketAccess));
    }

    public Result SetCors(CorsConfig cfg)
    {
        Cors = cfg;
        return durableWrite.AtomicReplace(corsPath, JsonSerializer.Serialize(cfg, CorsJsonContext.Default.CorsConfig));
    }

    public Result RemoveCors()
    {
        Cors = null;
        if (File.Exists(corsPath)) File.Delete(corsPath);
        return Result.Ok;
    }

    private VersioningStatus ReadVersioning() =>
        File.Exists(versioningPath)
            && Enum.TryParse<VersioningStatus>(File.ReadAllText(versioningPath).Trim(), out var s)
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
