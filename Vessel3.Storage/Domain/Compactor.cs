namespace Vessel3.Storage;

internal sealed record CompactionOutcome(long LogBytesBefore, long LogBytesAfter);

internal sealed record CompactionReport(int BucketsCompacted, int Failed, long LogBytesBefore, long LogBytesAfter);

internal interface ICompactor
{
    CompactionReport Run(long minLogBytes);
}

internal sealed class Compactor(IBucketRegistry registry) : ICompactor
{
    private readonly Lock gate = new();

    public CompactionReport Run(long minLogBytes)
    {
        lock (gate)
        {
            var compacted = 0;
            var failed = 0;
            long before = 0;
            long after = 0;

            foreach (var bucket in registry.OpenBuckets())
            {
                if (bucket.LogBytes() < minLogBytes) continue;
                try
                {
                    if (bucket.Compact().TryGetValue(out var outcome, out _))
                    {
                        compacted++;
                        before += outcome.LogBytesBefore;
                        after += outcome.LogBytesAfter;
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
                {
                    failed++;
                }
            }

            return new CompactionReport(compacted, failed, before, after);
        }
    }
}
