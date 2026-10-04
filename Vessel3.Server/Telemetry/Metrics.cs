using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Vessel3.Server.Telemetry;

internal interface IMetricsCollector
{
    void RecordRequest(string action, int status, long elapsedTicks, long reqBytes, long resBytes);
    void RecordStages(RequestTrace trace);
}

internal interface IMetricsRenderer
{
    string ContentType { get; }
    void Render(StringBuilder builder, IEnumerable<BucketStats> buckets);
}

internal interface IMetricsService : IMetricsCollector, IMetricsRenderer
{
}

internal sealed class MetricsService : IMetricsService
{
    public string ContentType => "text/plain; version=0.0.4; charset=utf-8";

    private readonly long startTimeUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static readonly string[] StatusNames = ["2xx", "3xx", "4xx", "5xx", "other"];
    private const int StatusCount = 5;

    private static readonly double[] RequestBuckets =
        [0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];
    private static readonly double[] StageBuckets =
        [0.0001, 0.00025, 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5];

    private static readonly string[] StageNames =
        ["gate_wait", "body", "blob_sync", "write_lock", "log_sync", "index_commit", "read_lock", "query"];
    private const int StageCount = 8;

    private sealed class Histogram(double[] bounds)
    {
        public readonly double[] Bounds = bounds;
        public readonly long[] Counts = new long[bounds.Length + 1];
        public long Count;
        public long SumTicks;

        public void Observe(long ticks)
        {
            Interlocked.Increment(ref Count);
            Interlocked.Add(ref SumTicks, ticks);
            var seconds = (double)ticks / Stopwatch.Frequency;
            var idx = Bounds.Length;
            for (var i = 0; i < Bounds.Length; i++)
            {
                if (seconds <= Bounds[i]) { idx = i; break; }
            }
            Interlocked.Increment(ref Counts[idx]);
        }
    }

    private sealed class ActionCounters
    {
        public readonly long[] Requests = new long[StatusCount];
        public long RequestBytes;
        public long ResponseBytes;
        public readonly Histogram Latency = new(RequestBuckets);
    }

    private readonly ConcurrentDictionary<string, ActionCounters> actions = new(StringComparer.Ordinal);
    private readonly Histogram[] stages = [.. Enumerable.Range(0, StageCount).Select(_ => new Histogram(StageBuckets))];

    public static int StatusIndex(int status) => status switch
    {
        >= 200 and < 300 => 0,
        >= 300 and < 400 => 1,
        >= 400 and < 500 => 2,
        >= 500 and < 600 => 3,
        _ => 4,
    };

    public void RecordRequest(string action, int status, long elapsedTicks, long reqBytes, long resBytes)
    {
        var counters = actions.GetOrAdd(action, static _ => new ActionCounters());
        Interlocked.Increment(ref counters.Requests[StatusIndex(status)]);
        if (reqBytes > 0) Interlocked.Add(ref counters.RequestBytes, reqBytes);
        if (resBytes > 0) Interlocked.Add(ref counters.ResponseBytes, resBytes);
        counters.Latency.Observe(elapsedTicks);
    }

    public void RecordStages(RequestTrace trace)
    {
        for (var i = 0; i < StageCount; i++)
        {
            var ticks = trace.Ticks((Stage)i);
            if (ticks > 0) stages[i].Observe(ticks);
        }
    }

    public void Render(StringBuilder builder, IEnumerable<BucketStats> buckets)
    {
        var culture = CultureInfo.InvariantCulture;

        RenderProcessAndGcMetrics(builder, culture);

        var sortedActions = actions.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        RenderActionMetrics(builder, sortedActions, culture);
        RenderStageMetrics(builder, culture);

        var stats = buckets.ToList();
        RenderBucketMetrics(builder, stats, culture);
    }

    private void RenderProcessAndGcMetrics(StringBuilder builder, CultureInfo culture)
    {
        using var process = Process.GetCurrentProcess();

        builder.Append("# HELP process_start_time_seconds Start time of the process since unix epoch in seconds.\n");
        builder.Append("# TYPE process_start_time_seconds gauge\n");
        builder.Append("process_start_time_seconds ").Append(startTimeUnixSeconds.ToString(culture)).Append('\n');

        builder.Append("# HELP process_resident_memory_bytes Resident memory size in bytes.\n");
        builder.Append("# TYPE process_resident_memory_bytes gauge\n");
        builder.Append("process_resident_memory_bytes ").Append(process.WorkingSet64.ToString(culture)).Append('\n');

        builder.Append("# HELP process_cpu_seconds_total Total user and system CPU time spent in seconds.\n");
        builder.Append("# TYPE process_cpu_seconds_total counter\n");
        builder.Append("process_cpu_seconds_total ")
            .Append(process.TotalProcessorTime.TotalSeconds.ToString("0.######", culture)).Append('\n');

        builder.Append("# HELP dotnet_gc_collections_total Total number of garbage collections by generation.\n");
        builder.Append("# TYPE dotnet_gc_collections_total counter\n");
        for (var generation = 0; generation <= GC.MaxGeneration; generation++)
        {
            builder.Append("dotnet_gc_collections_total{generation=\"").Append(generation.ToString(culture)).Append("\"} ")
                .Append(GC.CollectionCount(generation).ToString(culture)).Append('\n');
        }

        builder.Append("# HELP dotnet_gc_heap_bytes Bytes currently allocated on the managed heap.\n");
        builder.Append("# TYPE dotnet_gc_heap_bytes gauge\n");
        builder.Append("dotnet_gc_heap_bytes ").Append(GC.GetTotalMemory(forceFullCollection: false).ToString(culture)).Append('\n');
    }

    private static void RenderActionMetrics(StringBuilder builder, List<KeyValuePair<string, ActionCounters>> sortedActions, CultureInfo culture)
    {
        builder.Append("# HELP vessel3_requests_total Count of requests handled, by S3 action and status class.\n");
        builder.Append("# TYPE vessel3_requests_total counter\n");
        foreach (var (action, counters) in sortedActions)
        {
            for (var statusIndex = 0; statusIndex < StatusCount; statusIndex++)
            {
                var count = Interlocked.Read(ref counters.Requests[statusIndex]);
                if (count == 0) continue;
                builder.Append("vessel3_requests_total{action=\"").Append(action)
                    .Append("\",status=\"").Append(StatusNames[statusIndex]).Append("\"} ")
                    .Append(count.ToString(culture)).Append('\n');
            }
        }

        builder.Append("# HELP vessel3_request_bytes_total Total request body bytes received, by S3 action.\n");
        builder.Append("# TYPE vessel3_request_bytes_total counter\n");
        foreach (var (action, counters) in sortedActions)
        {
            var count = Interlocked.Read(ref counters.RequestBytes);
            if (count == 0) continue;
            builder.Append("vessel3_request_bytes_total{action=\"").Append(action).Append("\"} ").Append(count.ToString(culture)).Append('\n');
        }

        builder.Append("# HELP vessel3_response_bytes_total Total response body bytes sent, by S3 action.\n");
        builder.Append("# TYPE vessel3_response_bytes_total counter\n");
        foreach (var (action, counters) in sortedActions)
        {
            var count = Interlocked.Read(ref counters.ResponseBytes);
            if (count == 0) continue;
            builder.Append("vessel3_response_bytes_total{action=\"").Append(action).Append("\"} ").Append(count.ToString(culture)).Append('\n');
        }

        builder.Append("# HELP vessel3_request_duration_seconds Request latency histogram in seconds, by S3 action.\n");
        builder.Append("# TYPE vessel3_request_duration_seconds histogram\n");
        foreach (var (action, counters) in sortedActions)
        {
            RenderHistogram(builder, "vessel3_request_duration_seconds", "action", action, counters.Latency, culture);
        }
    }

    private void RenderStageMetrics(StringBuilder builder, CultureInfo culture)
    {
        builder.Append("# HELP vessel3_stage_duration_seconds Time spent per request in each storage stage, in seconds.\n");
        builder.Append("# TYPE vessel3_stage_duration_seconds histogram\n");
        for (var i = 0; i < StageCount; i++)
        {
            RenderHistogram(builder, "vessel3_stage_duration_seconds", "stage", StageNames[i], stages[i], culture);
        }
    }

    private static void RenderBucketMetrics(StringBuilder builder, List<BucketStats> stats, CultureInfo culture)
    {
        RenderGauge(builder, "vessel3_bucket_versions", "Rows in the bucket index (one per stored version).", stats, bucket => bucket.Versions, culture);
        RenderGauge(builder, "vessel3_bucket_index_bytes", "Size of the bucket's SQLite index file.", stats, bucket => bucket.IndexBytes, culture);
        RenderGauge(builder, "vessel3_bucket_wal_bytes", "Size of the bucket index's write-ahead log.", stats, bucket => bucket.WalBytes, culture);
        RenderGauge(builder, "vessel3_bucket_log_bytes", "Size of the bucket's event log.", stats, bucket => bucket.LogBytes, culture);
    }

    private static void RenderHistogram(StringBuilder builder, string name, string label, string value, Histogram histogram, CultureInfo culture)
    {
        var count = Interlocked.Read(ref histogram.Count);
        if (count == 0) return;
        long cumulative = 0;
        for (var i = 0; i < histogram.Bounds.Length; i++)
        {
            cumulative += Interlocked.Read(ref histogram.Counts[i]);
            builder.Append(name).Append("_bucket{").Append(label).Append("=\"").Append(value)
                .Append("\",le=\"").Append(histogram.Bounds[i].ToString("0.#####", culture)).Append("\"} ")
                .Append(cumulative.ToString(culture)).Append('\n');
        }
        cumulative += Interlocked.Read(ref histogram.Counts[histogram.Bounds.Length]);
        builder.Append(name).Append("_bucket{").Append(label).Append("=\"").Append(value).Append("\",le=\"+Inf\"} ")
            .Append(cumulative.ToString(culture)).Append('\n');
        var sumSeconds = (double)Interlocked.Read(ref histogram.SumTicks) / Stopwatch.Frequency;
        builder.Append(name).Append("_sum{").Append(label).Append("=\"").Append(value).Append("\"} ")
            .Append(sumSeconds.ToString("0.######", culture)).Append('\n');
        builder.Append(name).Append("_count{").Append(label).Append("=\"").Append(value).Append("\"} ")
            .Append(count.ToString(culture)).Append('\n');
    }

    private static void RenderGauge(StringBuilder builder, string name, string help, List<BucketStats> stats, Func<BucketStats, long> pick, CultureInfo culture)
    {
        builder.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        builder.Append("# TYPE ").Append(name).Append(" gauge\n");
        foreach (var bucket in stats)
        {
            builder.Append(name).Append("{bucket=\"").Append(bucket.Name).Append("\"} ").Append(pick(bucket).ToString(culture)).Append('\n');
        }
    }

    internal void ResetForTests()
    {
        actions.Clear();
        for (var i = 0; i < StageCount; i++) stages[i] = new Histogram(StageBuckets);
    }
}
