namespace Vessel3.Server;

internal sealed class BucketStatsCache(IBucketRegistry registry, TimeProvider clock, TimeSpan ttl)
{
    private readonly Lock sync = new();
    private IReadOnlyList<BucketStats> cached = [];
    private long refreshedAt = long.MinValue;

    public IReadOnlyList<BucketStats> Get()
    {
        lock (sync)
        {
            var now = clock.GetTimestamp();
            if (refreshedAt != long.MinValue && clock.GetElapsedTime(refreshedAt, now) < ttl) return cached;
            cached = [.. registry.OpenBuckets().Select(b => b.Stats())];
            refreshedAt = now;
            return cached;
        }
    }
}
