namespace Vessel3.Storage;

internal static class ObjectLockEvaluator
{
    public static Result<(IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome)> EvaluateHardDelete(
        IBucketIndex index, string key, string versionId, bool bypassGovernance)
    {
        var (retention, hasLegalHold) = index.GetObjectLockStatus(key, versionId);
        if (hasLegalHold)
        {
            return new AccessDeniedError($"legal hold on {key}@{versionId}");
        }

        if (CheckRetention(retention, bypassGovernance, key, versionId) is { } accessDenied)
        {
            return accessDenied;
        }

        IReadOnlyList<VersionEvent> events = [new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, versionId)];
        return (events, new DeleteOutcome(versionId, IsDeleteMarker: false, Found: true));
    }

    public static Result<(IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome)> EvaluateDelete(
        IBucketIndex index, VersioningStatus versioning, string key, bool bypassGovernance) => versioning switch
    {
        VersioningStatus.Enabled => EvaluateEnabledDelete(key),
        VersioningStatus.Suspended => EvaluateSuspendedDelete(index, key),
        VersioningStatus.Unversioned or _ => EvaluateUnversionedDelete(index, key, bypassGovernance),
    };

    private static (IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome) EvaluateEnabledDelete(string key)
    {
        var markerVersion = Ulid.NewUlid().ToString();
        IReadOnlyList<VersionEvent> events = [new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, markerVersion)];
        return (events, new DeleteOutcome(markerVersion, IsDeleteMarker: true, Found: true));
    }

    private static (IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome) EvaluateSuspendedDelete(IBucketIndex index, string key)
    {
        HardDeleteEvent? hardDelete = index.LatestVersionId(key) is "null"
            ? new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, "null")
            : null;
        var marker = new DeleteMarkerEvent(0, DateTimeOffset.UtcNow, key, "null");
        IReadOnlyList<VersionEvent> events = hardDelete is null ? [marker] : [hardDelete, marker];
        return (events, new DeleteOutcome("null", IsDeleteMarker: true, Found: true));
    }

    private static Result<(IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome)> EvaluateUnversionedDelete(
        IBucketIndex index, string key, bool bypassGovernance)
    {
        if (!index.GetCurrentPut(key).TryGetValue(out var currentPut, out _))
        {
            return ([], new DeleteOutcome(string.Empty, IsDeleteMarker: false, Found: false));
        }

        if (currentPut.LegalHoldOn)
        {
            return new AccessDeniedError($"legal hold on {key}");
        }

        if (CheckRetention(currentPut.Retention, bypassGovernance, key) is { } accessDenied)
        {
            return accessDenied;
        }

        IReadOnlyList<VersionEvent> events = [new HardDeleteEvent(0, DateTimeOffset.UtcNow, key, currentPut.VersionId)];
        return (events, new DeleteOutcome(currentPut.VersionId, IsDeleteMarker: false, Found: true));
    }

    private static AccessDeniedError? CheckRetention(Retention? retention, bool bypassGovernance, string key, string? versionId = null)
    {
        if (retention is null || retention.RetainUntilDate <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        var target = versionId is not null ? $"{key}@{versionId}" : key;
        if (retention.Mode is RetentionMode.Compliance)
        {
            return new AccessDeniedError($"COMPLIANCE retention on {target} until {retention.RetainUntilDate:O}");
        }

        if (!bypassGovernance)
        {
            return new AccessDeniedError($"GOVERNANCE retention on {target}; bypass header required");
        }

        return null;
    }
}
