namespace Vessel3.Storage;

internal static class ObjectLockEvaluator
{
    public static Result<(IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome)> EvaluateHardDelete(
        IBucketIndex index, string key, string versionId, bool bypassGovernance)
    {
        var (ret, hold) = index.GetObjectLockStatus(key, versionId);
        if (hold) return new AccessDeniedError($"legal hold on {key}@{versionId}");
        if (ret is not null && ret.RetainUntilDate > DateTimeOffset.UtcNow)
        {
            if (ret.Mode is RetentionMode.Compliance)
                return new AccessDeniedError($"COMPLIANCE retention on {key}@{versionId} until {ret.RetainUntilDate:O}");
            if (!bypassGovernance)
                return new AccessDeniedError($"GOVERNANCE retention on {key}@{versionId}; bypass header required");
        }
        IReadOnlyList<VersionEvent> events = [new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, versionId)];
        return (events, new DeleteOutcome(versionId, IsDeleteMarker: false, Found: true));
    }

    public static Result<(IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome)> EvaluateDelete(
        IBucketIndex index, VersioningStatus versioning, string key, bool bypassGovernance)
    {
        switch (versioning)
        {
            case VersioningStatus.Enabled:
            {
                var markerVersion = Ulid.NewUlid().ToString();
                IReadOnlyList<VersionEvent> events = [new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, markerVersion)];
                return (events, new DeleteOutcome(markerVersion, IsDeleteMarker: true, Found: true));
            }

            case VersioningStatus.Suspended:
            {
                HardDeleteEvent? hd = index.LatestVersionId(key) is "null"
                    ? new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, "null")
                    : null;
                var marker = new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, "null");
                IReadOnlyList<VersionEvent> events = hd is null ? [marker] : [hd, marker];
                return (events, new DeleteOutcome("null", IsDeleteMarker: true, Found: true));
            }

            case VersioningStatus.Unversioned:
            default:
            {
                if (!index.GetCurrentPut(key).TryGetValue(out var old, out _))
                    return ([], new DeleteOutcome(string.Empty, IsDeleteMarker: false, Found: false));

                if (old.LegalHoldOn)
                    return new AccessDeniedError($"legal hold on {key}");
                if (old.Retention is { } r && r.RetainUntilDate > DateTimeOffset.UtcNow)
                {
                    if (r.Mode is RetentionMode.Compliance)
                        return new AccessDeniedError($"COMPLIANCE retention on {key} until {r.RetainUntilDate:O}");
                    if (!bypassGovernance)
                        return new AccessDeniedError($"GOVERNANCE retention on {key}; bypass header required");
                }

                IReadOnlyList<VersionEvent> events = [new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, old.VersionId)];
                return (events, new DeleteOutcome(old.VersionId, IsDeleteMarker: false, Found: true));
            }
        }
    }
}
