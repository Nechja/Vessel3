using System.Collections.Concurrent;
using System.Diagnostics;

namespace Vessel3.Bench;

internal sealed class LatencyRecorder
{
    private readonly ConcurrentBag<double> latenciesMs = new();
    private long opCount;
    private long byteCount;

    public void Record(long elapsedTicks, long bytes = 0)
    {
        var ms = (double)elapsedTicks * 1000.0 / Stopwatch.Frequency;
        latenciesMs.Add(ms);
        Interlocked.Increment(ref opCount);
        if (bytes > 0) Interlocked.Add(ref byteCount, bytes);
    }

    public void RecordBatch(long elapsedTicks, int count, long bytes = 0)
    {
        var msPerOp = ((double)elapsedTicks * 1000.0 / Stopwatch.Frequency) / count;
        latenciesMs.Add(msPerOp);
        Interlocked.Add(ref opCount, count);
        if (bytes > 0) Interlocked.Add(ref byteCount, bytes);
    }

    public LatencySummary Summarize(TimeSpan wallTime)
    {
        var sample = latenciesMs.ToArray();
        Array.Sort(sample);
        var n = sample.Length;

        double Pct(double p) => n is 0 ? 0 : sample[Math.Min((int)(n * p), n - 1)];

        return new LatencySummary(
            Ops: opCount,
            Bytes: byteCount,
            WallTime: wallTime,
            OpsPerSecond: opCount / wallTime.TotalSeconds,
            BytesPerSecond: byteCount / wallTime.TotalSeconds,
            P50Ms: Pct(0.50),
            P95Ms: Pct(0.95),
            P99Ms: Pct(0.99),
            P999Ms: Pct(0.999),
            MaxMs: n is 0 ? 0 : sample[n - 1],
            AvgMs: n is 0 ? 0 : sample.Average());
    }
}

internal sealed record LatencySummary(
    long Ops,
    long Bytes,
    TimeSpan WallTime,
    double OpsPerSecond,
    double BytesPerSecond,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double P999Ms,
    double MaxMs,
    double AvgMs);
