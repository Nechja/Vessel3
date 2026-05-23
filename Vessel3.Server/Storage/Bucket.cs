using System.Text.Json;
using Vessel3.Server;

namespace Vessel3.Server.Storage;

internal enum VersioningStatus { Unversioned, Enabled, Suspended }

internal sealed class Bucket(string name, string path) : IDisposable
{
    private readonly VersionLog log = new(Path.Combine(path, "log"));
    private readonly string versioningPath = Path.Combine(path, "versioning.txt");
    private readonly string objectLockPath = Path.Combine(path, "object-lock.json");
    private readonly Lock writeGate = new();

    public string Name { get; } = name;
    public BucketIndex Index { get; } = new(Path.Combine(path, "index.db"));
    public DateTimeOffset CreatedAt { get; private set; }
    public VersioningStatus Versioning { get; private set; }
    /// Null when never configured; non-null thereafter. Once `Enabled = true`
    /// it can be re-PUT to refresh the default rule but cannot be turned off.
    public ObjectLockConfig? ObjectLock { get; private set; }

    public void Open()
    {
        Index.Open();
        CreatedAt = Directory.GetCreationTimeUtc(path);
        Versioning = ReadVersioning();
        ObjectLock = ReadObjectLock();

        var maxSeq = Index.MaxSeq();
        foreach (var ev in log.Replay())
        {
            if (ev.Seq <= maxSeq) continue;
            ev.ApplyTo(Index);
            maxSeq = ev.Seq;
        }

        log.Open(maxSeq + 1);
    }

    public Result<bool> SetVersioning(VersioningStatus status)
    {
        // Once Object Lock is enabled, AWS forbids suspending versioning;
        // returning the bucket to an unversioned state would break retention.
        if (ObjectLock is { Enabled: true }
            && status is VersioningStatus.Suspended or VersioningStatus.Unversioned)
            return new InvalidBucketStateError("cannot suspend versioning on a bucket with Object Lock enabled");

        Versioning = status;
        if (status is VersioningStatus.Unversioned)
        {
            if (File.Exists(versioningPath)) File.Delete(versioningPath);
            return true;
        }
        var tmp = versioningPath + ".tmp";
        File.WriteAllText(tmp, status.ToString());
        File.Move(tmp, versioningPath, overwrite: true);
        return true;
    }

    public Result<bool> SetObjectLock(ObjectLockConfig cfg)
    {
        // AWS: Object Lock can only be enabled when versioning is Enabled.
        if (cfg.Enabled && Versioning is not VersioningStatus.Enabled)
            return new InvalidBucketStateError("Object Lock requires versioning to be Enabled");
        // Once Enabled, cannot be disabled.
        if (ObjectLock is { Enabled: true } && !cfg.Enabled)
            return new InvalidBucketStateError("Object Lock cannot be disabled once enabled");

        ObjectLock = cfg;
        var tmp = objectLockPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, ObjectLockJsonContext.Default.ObjectLockConfig));
        File.Move(tmp, objectLockPath, overwrite: true);
        return true;
    }

    private VersioningStatus ReadVersioning() =>
        File.Exists(versioningPath)
            && Enum.TryParse<VersioningStatus>(File.ReadAllText(versioningPath).Trim(), out var s)
                ? s : VersioningStatus.Unversioned;

    private ObjectLockConfig? ReadObjectLock() =>
        File.Exists(objectLockPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(objectLockPath), ObjectLockJsonContext.Default.ObjectLockConfig)
            : null;

    /// <summary>
    /// Writes a new object version. State machine driven by <see cref="Versioning"/>:
    /// <list type="bullet">
    ///   <item><b>Unversioned</b>: hard-delete prior put (if any), insert with fresh Ulid version_id.</item>
    ///   <item><b>Enabled</b>: append-only, fresh Ulid version_id, prior versions retained.</item>
    ///   <item><b>Suspended</b>: version_id is literal "null". If the latest row for this key is itself
    ///     version_id="null" (put OR delete-marker), hard-delete it first to satisfy the
    ///     (key, version_id) UNIQUE constraint. Non-"null" prior versions are preserved.</item>
    /// </list>
    /// </summary>
    public PutEntry AppendPut(string key, PutRequest req)
    {
        lock (writeGate)
        {
            switch (Versioning)
            {
                case VersioningStatus.Unversioned:
                    if (Index.GetCurrentPut(key) is Result<PutEntry?>.Success { Value: { } oldU })
                        log.Append(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, oldU.VersionId)).ApplyTo(Index);
                    break;
                case VersioningStatus.Suspended:
                    if (LatestVersionId(key) is "null")
                        log.Append(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, "null")).ApplyTo(Index);
                    break;
                case VersioningStatus.Enabled:
                default:
                    break;
            }

            var versionId = Versioning is VersioningStatus.Suspended ? "null" : Ulid.NewUlid().ToString();
            var ev = (PutEvent)log.Append(new PutEvent(
                0, DateTimeOffset.UtcNow, key, versionId,
                req.BlobSha, req.Md5, req.Size, req.ContentType, req.Metadata, req.Parts,
                req.Tags, req.Crc32, req.Crc32C, req.Sha1,
                RetentionMode: req.Retention?.Mode,
                RetainUntilUnixSeconds: req.Retention?.RetainUntilDate.ToUnixTimeSeconds(),
                LegalHoldOn: req.LegalHoldOn));
            ev.ApplyTo(Index);

            return new PutEntry(versionId, ev.At, req.BlobSha, req.Md5, req.Size, req.ContentType, req.Metadata, req.Parts,
                req.Tags, req.Crc32, req.Crc32C, req.Sha1,
                Retention: req.Retention, LegalHoldOn: req.LegalHoldOn);
        }
    }

    public PutTaggingOutcome AppendPutTagging(string key, string versionId, IReadOnlyDictionary<string, string> tags)
    {
        lock (writeGate)
        {
            log.Append(new PutTaggingEvent(0, DateTimeOffset.UtcNow, key, versionId, tags)).ApplyTo(Index);
            return new PutTaggingOutcome(versionId);
        }
    }

    public Result<DeleteOutcome> HardDeleteVersion(string key, string versionId, bool bypassGovernance)
    {
        lock (writeGate)
        {
            // Enforce hold/retention on the exact targeted version.
            var (ret, hold) = Index.GetLock(key, versionId);
            if (hold) return new AccessDeniedError($"legal hold on {key}@{versionId}");
            if (ret is not null && ret.RetainUntilDate > DateTimeOffset.UtcNow)
            {
                if (ret.Mode is RetentionMode.Compliance)
                    return new AccessDeniedError($"COMPLIANCE retention on {key}@{versionId} until {ret.RetainUntilDate:O}");
                if (!bypassGovernance)
                    return new AccessDeniedError($"GOVERNANCE retention on {key}@{versionId}; bypass header required");
            }
            log.Append(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, versionId)).ApplyTo(Index);
            return new DeleteOutcome(versionId, IsDeleteMarker: false, Found: true);
        }
    }

    /// <summary>
    /// Deletes per <see cref="Versioning"/>:
    /// <list type="bullet">
    ///   <item><b>Unversioned</b>: hard-delete current put; 404 if absent. Retention/legal-hold enforced.</item>
    ///   <item><b>Enabled</b>: append a delete marker with fresh Ulid version_id.</item>
    ///   <item><b>Suspended</b>: append a delete marker with version_id="null". If the latest row is
    ///     already version_id="null" (put or marker), hard-delete it first. Older non-"null"
    ///     versions remain intact.</item>
    /// </list>
    /// </summary>
    public Result<DeleteOutcome> AppendDelete(string key, bool bypassGovernance)
    {
        lock (writeGate)
        {
            switch (Versioning)
            {
                case VersioningStatus.Enabled:
                {
                    // Versioned DELETE writes a delete marker — it does not remove
                    // any existing version, so retention/hold are not breached.
                    var markerVersion = Ulid.NewUlid().ToString();
                    log.Append(new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, markerVersion)).ApplyTo(Index);
                    return new DeleteOutcome(markerVersion, IsDeleteMarker: true, Found: true);
                }

                case VersioningStatus.Suspended:
                {
                    if (LatestVersionId(key) is "null")
                        log.Append(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, "null")).ApplyTo(Index);
                    log.Append(new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, "null")).ApplyTo(Index);
                    return new DeleteOutcome("null", IsDeleteMarker: true, Found: true);
                }

                case VersioningStatus.Unversioned:
                default:
                {
                    if (Index.GetCurrentPut(key) is not Result<PutEntry?>.Success { Value: { } old })
                        return new DeleteOutcome(string.Empty, IsDeleteMarker: false, Found: false);

                    // Unversioned delete actually removes bytes; treat as hard delete.
                    if (old.LegalHoldOn)
                        return new AccessDeniedError($"legal hold on {key}");
                    if (old.Retention is { } r && r.RetainUntilDate > DateTimeOffset.UtcNow)
                    {
                        if (r.Mode is RetentionMode.Compliance)
                            return new AccessDeniedError($"COMPLIANCE retention on {key} until {r.RetainUntilDate:O}");
                        if (!bypassGovernance)
                            return new AccessDeniedError($"GOVERNANCE retention on {key}; bypass header required");
                    }

                    log.Append(new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, old.VersionId)).ApplyTo(Index);
                    return new DeleteOutcome(old.VersionId, IsDeleteMarker: false, Found: true);
                }
            }
        }
    }

    private string? LatestVersionId(string key) => Index.LatestVersionId(key);

    public Result<bool> PutRetention(string key, string versionId, Retention next, bool bypassGovernance)
    {
        lock (writeGate)
        {
            // The version must exist as a Put — retention on delete markers is nonsensical.
            if (Index.GetVersion(key, versionId) is not Result<PutEntry?>.Success { Value: { } })
                return new NotFoundError($"{key}@{versionId}");

            var (current, _) = Index.GetLock(key, versionId);
            if (current is not null)
            {
                var lowering = next.RetainUntilDate < current.RetainUntilDate
                            || next.Mode is RetentionMode.Governance && current.Mode is RetentionMode.Compliance;
                if (current.Mode is RetentionMode.Compliance && lowering)
                    return new AccessDeniedError("COMPLIANCE retention cannot be shortened or downgraded");
                if (current.Mode is RetentionMode.Governance && lowering && !bypassGovernance)
                    return new AccessDeniedError("GOVERNANCE retention can only be shortened with the bypass header");
            }
            log.Append(new PutRetentionEvent(
                0, DateTimeOffset.UtcNow, key, versionId, next.Mode, next.RetainUntilDate.ToUnixTimeSeconds())).ApplyTo(Index);
            return true;
        }
    }

    public Result<bool> PutLegalHold(string key, string versionId, bool on)
    {
        lock (writeGate)
        {
            if (Index.GetVersion(key, versionId) is not Result<PutEntry?>.Success { Value: { } })
                return new NotFoundError($"{key}@{versionId}");
            log.Append(new PutLegalHoldEvent(0, DateTimeOffset.UtcNow, key, versionId, on)).ApplyTo(Index);
            return true;
        }
    }

    public bool IsEmpty() => Index.IsEmpty();

    public void Dispose()
    {
        log.Dispose();
        Index.Dispose();
    }
}
