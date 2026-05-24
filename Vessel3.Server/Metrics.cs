using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Vessel3.Server;

internal static class Metrics
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    private static readonly long StartTimeUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static readonly string[] MethodNames = ["GET", "PUT", "POST", "DELETE", "HEAD", "OTHER"];
    private const int MethodCount = 6;

    private static readonly string[] StatusNames = ["2xx", "3xx", "4xx", "5xx", "other"];
    private const int StatusCount = 5;

    // Histogram bucket upper bounds in seconds. The last bucket is +Inf, stored at index BucketCount.
    private static readonly double[] LatencyBuckets =
        [0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];
    private static readonly int BucketCount = LatencyBuckets.Length;

    private static readonly long[] RequestsTotal = new long[MethodCount * StatusCount];
    private static readonly long[] RequestBytes = new long[MethodCount];
    private static readonly long[] ResponseBytes = new long[MethodCount];

    // Cumulative bucket counts. Flat layout: methodIdx * (BucketCount + 1) + bucketIdx.
    private static readonly long[] LatencyBucketCounts = new long[MethodCount * (LatencyBuckets.Length + 1)];
    private static readonly long[] LatencyCount = new long[MethodCount];
    private static readonly long[] LatencySumTicks = new long[MethodCount];

    public static int MethodIndex(string method) => method switch
    {
        "GET" => 0,
        "PUT" => 1,
        "POST" => 2,
        "DELETE" => 3,
        "HEAD" => 4,
        _ => 5,
    };

    public static int StatusIndex(int status) => status switch
    {
        >= 200 and < 300 => 0,
        >= 300 and < 400 => 1,
        >= 400 and < 500 => 2,
        >= 500 and < 600 => 3,
        _ => 4,
    };

    public static void RecordRequest(int methodIdx, int statusIdx, long elapsedTicks, long reqBytes, long resBytes)
    {
        Interlocked.Increment(ref RequestsTotal[methodIdx * StatusCount + statusIdx]);
        if (reqBytes > 0) Interlocked.Add(ref RequestBytes[methodIdx], reqBytes);
        if (resBytes > 0) Interlocked.Add(ref ResponseBytes[methodIdx], resBytes);

        Interlocked.Increment(ref LatencyCount[methodIdx]);
        Interlocked.Add(ref LatencySumTicks[methodIdx], elapsedTicks);

        var seconds = (double)elapsedTicks / Stopwatch.Frequency;
        var bucketIdx = BucketCount;
        for (var i = 0; i < BucketCount; i++)
        {
            if (seconds <= LatencyBuckets[i]) { bucketIdx = i; break; }
        }
        var rowBase = methodIdx * (BucketCount + 1);
        for (var i = bucketIdx; i <= BucketCount; i++)
        {
            Interlocked.Increment(ref LatencyBucketCounts[rowBase + i]);
        }
    }

    public static void Render(StringBuilder sb)
    {
        var inv = CultureInfo.InvariantCulture;

        using var proc = Process.GetCurrentProcess();

        sb.Append("# HELP process_start_time_seconds Start time of the process since unix epoch in seconds.\n");
        sb.Append("# TYPE process_start_time_seconds gauge\n");
        sb.Append("process_start_time_seconds ").Append(StartTimeUnixSeconds.ToString(inv)).Append('\n');

        sb.Append("# HELP process_resident_memory_bytes Resident memory size in bytes.\n");
        sb.Append("# TYPE process_resident_memory_bytes gauge\n");
        sb.Append("process_resident_memory_bytes ").Append(proc.WorkingSet64.ToString(inv)).Append('\n');

        sb.Append("# HELP process_cpu_seconds_total Total user and system CPU time spent in seconds.\n");
        sb.Append("# TYPE process_cpu_seconds_total counter\n");
        sb.Append("process_cpu_seconds_total ")
            .Append(proc.TotalProcessorTime.TotalSeconds.ToString("0.######", inv)).Append('\n');

        sb.Append("# HELP dotnet_gc_collections_total Total number of garbage collections by generation.\n");
        sb.Append("# TYPE dotnet_gc_collections_total counter\n");
        for (var gen = 0; gen <= GC.MaxGeneration; gen++)
        {
            sb.Append("dotnet_gc_collections_total{generation=\"").Append(gen.ToString(inv)).Append("\"} ")
                .Append(GC.CollectionCount(gen).ToString(inv)).Append('\n');
        }

        sb.Append("# HELP dotnet_gc_heap_bytes Bytes currently allocated on the managed heap.\n");
        sb.Append("# TYPE dotnet_gc_heap_bytes gauge\n");
        sb.Append("dotnet_gc_heap_bytes ").Append(GC.GetTotalMemory(forceFullCollection: false).ToString(inv)).Append('\n');

        sb.Append("# HELP vessel3_http_requests_total Count of HTTP requests handled, by method and status class.\n");
        sb.Append("# TYPE vessel3_http_requests_total counter\n");
        for (var m = 0; m < MethodCount; m++)
        {
            for (var s = 0; s < StatusCount; s++)
            {
                var v = Interlocked.Read(ref RequestsTotal[m * StatusCount + s]);
                if (v == 0) continue;
                sb.Append("vessel3_http_requests_total{method=\"").Append(MethodNames[m])
                    .Append("\",status=\"").Append(StatusNames[s]).Append("\"} ")
                    .Append(v.ToString(inv)).Append('\n');
            }
        }

        sb.Append("# HELP vessel3_http_request_bytes_total Total request body bytes received, by method.\n");
        sb.Append("# TYPE vessel3_http_request_bytes_total counter\n");
        for (var m = 0; m < MethodCount; m++)
        {
            var v = Interlocked.Read(ref RequestBytes[m]);
            if (v == 0) continue;
            sb.Append("vessel3_http_request_bytes_total{method=\"").Append(MethodNames[m]).Append("\"} ")
                .Append(v.ToString(inv)).Append('\n');
        }

        sb.Append("# HELP vessel3_http_response_bytes_total Total response body bytes sent, by method.\n");
        sb.Append("# TYPE vessel3_http_response_bytes_total counter\n");
        for (var m = 0; m < MethodCount; m++)
        {
            var v = Interlocked.Read(ref ResponseBytes[m]);
            if (v == 0) continue;
            sb.Append("vessel3_http_response_bytes_total{method=\"").Append(MethodNames[m]).Append("\"} ")
                .Append(v.ToString(inv)).Append('\n');
        }

        sb.Append("# HELP vessel3_http_request_duration_seconds Request latency histogram in seconds, by method.\n");
        sb.Append("# TYPE vessel3_http_request_duration_seconds histogram\n");
        for (var m = 0; m < MethodCount; m++)
        {
            var count = Interlocked.Read(ref LatencyCount[m]);
            if (count == 0) continue;
            var rowBase = m * (BucketCount + 1);
            for (var b = 0; b < BucketCount; b++)
            {
                sb.Append("vessel3_http_request_duration_seconds_bucket{method=\"").Append(MethodNames[m])
                    .Append("\",le=\"").Append(LatencyBuckets[b].ToString("0.###", inv)).Append("\"} ")
                    .Append(Interlocked.Read(ref LatencyBucketCounts[rowBase + b]).ToString(inv)).Append('\n');
            }
            sb.Append("vessel3_http_request_duration_seconds_bucket{method=\"").Append(MethodNames[m])
                .Append("\",le=\"+Inf\"} ")
                .Append(Interlocked.Read(ref LatencyBucketCounts[rowBase + BucketCount]).ToString(inv)).Append('\n');
            var sumSec = (double)Interlocked.Read(ref LatencySumTicks[m]) / Stopwatch.Frequency;
            sb.Append("vessel3_http_request_duration_seconds_sum{method=\"").Append(MethodNames[m]).Append("\"} ")
                .Append(sumSec.ToString("0.######", inv)).Append('\n');
            sb.Append("vessel3_http_request_duration_seconds_count{method=\"").Append(MethodNames[m]).Append("\"} ")
                .Append(count.ToString(inv)).Append('\n');
        }
    }

    // Test helper. Resets all counters; not exposed for production callers.
    internal static void ResetForTests()
    {
        Array.Clear(RequestsTotal);
        Array.Clear(RequestBytes);
        Array.Clear(ResponseBytes);
        Array.Clear(LatencyBucketCounts);
        Array.Clear(LatencyCount);
        Array.Clear(LatencySumTicks);
    }
}
