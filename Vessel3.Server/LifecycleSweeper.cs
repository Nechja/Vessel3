using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record LifecycleReport(int Expired, int MarkersReaped);

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

            foreach (var bucket in registry.OpenBuckets())
            {
                if (bucket.Lifecycle is not { } cfg) continue;

                var (puts, markers) = Evaluate(bucket, cfg, now);

                foreach (var p in puts)
                    if (bucket.ExpireCurrentVersion(p.Key, p.VersionId, p.At)) expired++;

                foreach (var m in markers)
                    if (bucket.ReapExpiredDeleteMarker(m.Key, m.VersionId)) reaped++;
            }

            return new LifecycleReport(expired, reaped);
        }
    }

    private static (List<AllVersionsEntry.Put> Puts, List<AllVersionsEntry.Marker> Markers)
        Evaluate(Bucket bucket, LifecycleConfig cfg, DateTimeOffset now)
    {
        var puts = new List<AllVersionsEntry.Put>();
        var markers = new List<AllVersionsEntry.Marker>();

        foreach (var rule in cfg.Rules)
        {
            if (!rule.Enabled) continue;
            var prefix = string.IsNullOrEmpty(rule.Prefix) ? null : rule.Prefix;

            foreach (var entry in EnumerateAllVersions(bucket, prefix))
            {
                if (!entry.IsLatest) continue;

                if (rule.ExpirationDays is { } days
                    && entry is AllVersionsEntry.Put put
                    && now - put.At >= TimeSpan.FromDays(days))
                    puts.Add(put);
                else if (rule.ExpiredObjectDeleteMarker
                    && entry is AllVersionsEntry.Marker mk
                    && bucket.Index.CountVersions(mk.Key) is 1)
                    markers.Add(mk);
            }
        }

        return (puts, markers);
    }

    private static IEnumerable<AllVersionsEntry> EnumerateAllVersions(Bucket bucket, string? prefix)
    {
        string? keyMarker = null;
        while (true)
        {
            var (entries, truncated) = bucket.Index.ListAllVersions(prefix, keyMarker, 1000);
            foreach (var e in entries) yield return e;
            if (!truncated || entries.Count is 0) yield break;
            keyMarker = entries[^1].Key;
        }
    }
}
