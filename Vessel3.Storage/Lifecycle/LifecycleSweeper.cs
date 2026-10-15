namespace Vessel3.Storage.Lifecycle;

internal sealed record LifecycleReport(int Expired, int MarkersReaped, int NoncurrentExpired = 0);

internal sealed record ExpirationPlan(
    List<AllVersionsEntry.Put> CurrentVersionsToExpire,
    List<AllVersionsEntry.Marker> DeleteMarkersToReap,
    List<AllVersionsEntry> NoncurrentVersionsToExpire);

internal interface ILifecycleSweeper
{
    LifecycleReport Run(DateTimeOffset now);
}

internal sealed class LifecycleSweeper(IBucketRegistry registry) : ILifecycleSweeper
{
    private readonly Lock gate = new();

    public LifecycleReport Run(DateTimeOffset now)
    {
        lock (gate)
        {
            var expired = 0;
            var reaped = 0;
            var noncurrentExpired = 0;

            foreach (var bucket in registry.OpenBuckets())
            {
                if (bucket.Lifecycle is not { } config) continue;

                var plan = PlanBucketExpirations(bucket, config, now);
                expired += ExpireCurrentVersions(bucket, plan.CurrentVersionsToExpire);
                reaped += ReapDeleteMarkers(bucket, plan.DeleteMarkersToReap);
                noncurrentExpired += ExpireNoncurrentVersions(bucket, plan.NoncurrentVersionsToExpire);
            }

            return new LifecycleReport(expired, reaped, noncurrentExpired);
        }
    }

    private static int ExpireCurrentVersions(Bucket bucket, List<AllVersionsEntry.Put> currentVersions)
    {
        var count = 0;
        foreach (var put in currentVersions)
            if (bucket.ExpireCurrentVersion(put.Key, put.VersionId, put.At)) count++;
        return count;
    }

    private static int ReapDeleteMarkers(Bucket bucket, List<AllVersionsEntry.Marker> markers)
    {
        var count = 0;
        foreach (var marker in markers)
            if (bucket.ReapExpiredDeleteMarker(marker.Key, marker.VersionId)) count++;
        return count;
    }

    private static int ExpireNoncurrentVersions(Bucket bucket, List<AllVersionsEntry> noncurrentVersions)
    {
        var count = 0;
        foreach (var noncurrent in noncurrentVersions)
            if (bucket.HardDeleteVersion(noncurrent.Key, noncurrent.VersionId, bypassGovernance: false) is Result<DeleteOutcome>.Success)
                count++;
        return count;
    }

    private static ExpirationPlan PlanBucketExpirations(Bucket bucket, LifecycleConfig config, DateTimeOffset now)
    {
        List<AllVersionsEntry.Put> currentVersions = [];
        List<AllVersionsEntry.Marker> deleteMarkers = [];
        List<AllVersionsEntry> noncurrentVersions = [];
        HashSet<(string Key, string VersionId)> visitedNoncurrent = [];

        foreach (var rule in config.Rules)
        {
            if (!rule.Enabled) continue;
            var prefix = string.IsNullOrEmpty(rule.Prefix) ? null : rule.Prefix;

            string? activeKey = null;
            DateTimeOffset successorTimestamp = default;

            foreach (var entry in EnumerateAllVersions(bucket, prefix))
            {
                if (entry.Key != activeKey)
                {
                    activeKey = entry.Key;
                    successorTimestamp = entry.At;
                }

                if (entry.IsLatest)
                {
                    EvaluateLatestEntry(rule, entry, bucket, now, currentVersions, deleteMarkers);
                    continue;
                }

                EvaluateNoncurrentEntry(rule, entry, successorTimestamp, now, visitedNoncurrent, noncurrentVersions);
                successorTimestamp = entry.At;
            }
        }

        return new ExpirationPlan(currentVersions, deleteMarkers, noncurrentVersions);
    }

    private static void EvaluateLatestEntry(
        LifecycleRule rule,
        AllVersionsEntry entry,
        Bucket bucket,
        DateTimeOffset now,
        List<AllVersionsEntry.Put> currentVersions,
        List<AllVersionsEntry.Marker> deleteMarkers)
    {
        if (rule.ExpirationDays is { } days && entry is AllVersionsEntry.Put put && now - put.At >= TimeSpan.FromDays(days))
        {
            currentVersions.Add(put);
            return;
        }

        if (rule.ExpiredObjectDeleteMarker && entry is AllVersionsEntry.Marker marker && bucket.Index.CountVersions(marker.Key) is 1)
        {
            deleteMarkers.Add(marker);
        }
    }

    private static void EvaluateNoncurrentEntry(
        LifecycleRule rule,
        AllVersionsEntry entry,
        DateTimeOffset successorTimestamp,
        DateTimeOffset now,
        HashSet<(string Key, string VersionId)> visitedNoncurrent,
        List<AllVersionsEntry> noncurrentVersions)
    {
        if (rule.NoncurrentDays is not { } noncurrentDays) return;
        if (now - successorTimestamp < TimeSpan.FromDays(noncurrentDays)) return;
        if (visitedNoncurrent.Add((entry.Key, entry.VersionId)))
        {
            noncurrentVersions.Add(entry);
        }
    }

    private static IEnumerable<AllVersionsEntry> EnumerateAllVersions(Bucket bucket, string? prefix)
    {
        string? keyMarker = null;
        while (true)
        {
            var (entries, truncated) = bucket.Index.ListAllVersions(prefix, keyMarker, 1000);
            foreach (var entry in entries) yield return entry;
            if (!truncated || entries.Count is 0) yield break;
            keyMarker = entries[^1].Key;
        }
    }
}
