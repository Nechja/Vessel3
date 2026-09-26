using System.Diagnostics;
using System.Text;
using Vessel3.Server;
using Vessel3.Server.Telemetry;
using Xunit;

namespace Vessel3.Tests;

public class MetricsTests
{
    private readonly MetricsService metrics = new();

    private string Render(params BucketStats[] buckets)
    {
        var sb = new StringBuilder();
        metrics.Render(sb, buckets);
        return sb.ToString();
    }

    [Fact]
    public void StatusIndex_Classes()
    {
        Assert.Equal(0, MetricsService.StatusIndex(200));
        Assert.Equal(0, MetricsService.StatusIndex(204));
        Assert.Equal(1, MetricsService.StatusIndex(304));
        Assert.Equal(2, MetricsService.StatusIndex(403));
        Assert.Equal(2, MetricsService.StatusIndex(499));
        Assert.Equal(3, MetricsService.StatusIndex(500));
        Assert.Equal(4, MetricsService.StatusIndex(0));
        Assert.Equal(4, MetricsService.StatusIndex(700));
    }

    [Fact]
    public void Render_EmitsSeriesByAction()
    {
        metrics.RecordRequest("PutObject", 200, elapsedTicks: 0, reqBytes: 123, resBytes: 0);
        metrics.RecordRequest("GetObject", 404, elapsedTicks: 0, reqBytes: 0, resBytes: 17);
        metrics.RecordRequest("GetObject", 200, elapsedTicks: 0, reqBytes: 0, resBytes: 40);

        var text = Render();

        Assert.Contains("# TYPE vessel3_requests_total counter", text);
        Assert.Contains("vessel3_requests_total{action=\"PutObject\",status=\"2xx\"} 1", text);
        Assert.Contains("vessel3_requests_total{action=\"GetObject\",status=\"4xx\"} 1", text);
        Assert.Contains("vessel3_requests_total{action=\"GetObject\",status=\"2xx\"} 1", text);
        Assert.Contains("vessel3_request_bytes_total{action=\"PutObject\"} 123", text);
        Assert.Contains("vessel3_response_bytes_total{action=\"GetObject\"} 57", text);
        Assert.Contains("# TYPE vessel3_request_duration_seconds histogram", text);
        Assert.Contains("vessel3_request_duration_seconds_count{action=\"PutObject\"} 1", text);
        Assert.Contains("vessel3_request_duration_seconds_count{action=\"GetObject\"} 2", text);
        Assert.Contains("vessel3_request_duration_seconds_bucket{action=\"PutObject\",le=\"+Inf\"} 1", text);
        Assert.Contains("# TYPE process_resident_memory_bytes gauge", text);
        Assert.Contains("dotnet_gc_collections_total{generation=\"0\"}", text);
        Assert.DoesNotContain("method=", text);
    }

    [Fact]
    public void Render_OmitsZeroSeries()
    {
        metrics.RecordRequest("HeadObject", 200, 0, 0, 0);
        var text = Render();
        Assert.DoesNotContain("vessel3_requests_total{action=\"GetObject\"", text);
        Assert.DoesNotContain("vessel3_request_bytes_total{action=\"HeadObject\"}", text);
        Assert.DoesNotContain("vessel3_stage_duration_seconds_count", text);
    }

    [Fact]
    public void Histogram_BucketsAreCumulative()
    {
        metrics.RecordRequest("GetObject", 200, elapsedTicks: 0, reqBytes: 0, resBytes: 0);
        metrics.RecordRequest("GetObject", 200, elapsedTicks: Stopwatch.Frequency / 10, reqBytes: 0, resBytes: 0);
        var text = Render();
        Assert.Contains("vessel3_request_duration_seconds_bucket{action=\"GetObject\",le=\"0.001\"} 1", text);
        Assert.Contains("vessel3_request_duration_seconds_bucket{action=\"GetObject\",le=\"0.1\"} 2", text);
        Assert.Contains("vessel3_request_duration_seconds_bucket{action=\"GetObject\",le=\"10\"} 2", text);
        Assert.Contains("vessel3_request_duration_seconds_bucket{action=\"GetObject\",le=\"+Inf\"} 2", text);
        Assert.Contains("vessel3_request_duration_seconds_sum{action=\"GetObject\"} 0.1", text);
    }

    [Fact]
    public void Stages_RenderOnlyObservedStages()
    {
        var trace = new RequestTrace();
        trace.Add(Stage.LogSync, Stopwatch.Frequency / 200);
        trace.Add(Stage.Query, Stopwatch.Frequency / 20000);
        metrics.RecordStages(trace);

        var text = Render();

        Assert.Contains("# TYPE vessel3_stage_duration_seconds histogram", text);
        Assert.Contains("vessel3_stage_duration_seconds_count{stage=\"log_sync\"} 1", text);
        Assert.Contains("vessel3_stage_duration_seconds_bucket{stage=\"log_sync\",le=\"0.005\"} 1", text);
        Assert.Contains("vessel3_stage_duration_seconds_bucket{stage=\"log_sync\",le=\"0.0025\"} 0", text);
        Assert.Contains("vessel3_stage_duration_seconds_bucket{stage=\"query\",le=\"0.0001\"} 1", text);
        Assert.DoesNotContain("stage=\"body\"", text);
        Assert.DoesNotContain("stage=\"gate_wait\"", text);
    }

    [Fact]
    public void BucketGauges_RenderPerBucket()
    {
        var text = Render(
            new BucketStats("loki-chunks", 123456, 169_000_000, 4_096_000, 12_345),
            new BucketStats("skycam", 7, 40_960, 0, 0));

        Assert.Contains("# TYPE vessel3_bucket_versions gauge", text);
        Assert.Contains("vessel3_bucket_versions{bucket=\"loki-chunks\"} 123456", text);
        Assert.Contains("vessel3_bucket_index_bytes{bucket=\"loki-chunks\"} 169000000", text);
        Assert.Contains("vessel3_bucket_wal_bytes{bucket=\"loki-chunks\"} 4096000", text);
        Assert.Contains("vessel3_bucket_log_bytes{bucket=\"loki-chunks\"} 12345", text);
        Assert.Contains("vessel3_bucket_versions{bucket=\"skycam\"} 7", text);
        Assert.Contains("vessel3_bucket_wal_bytes{bucket=\"skycam\"} 0", text);
    }

    [Fact]
    public void Actions_RenderInStableOrder()
    {
        metrics.RecordRequest("PutObject", 200, 0, 0, 0);
        metrics.RecordRequest("DeleteObject", 204, 0, 0, 0);
        metrics.RecordRequest("ListObjects", 200, 0, 0, 0);
        var text = Render();
        var d = text.IndexOf("action=\"DeleteObject\"", StringComparison.Ordinal);
        var l = text.IndexOf("action=\"ListObjects\"", StringComparison.Ordinal);
        var p = text.IndexOf("action=\"PutObject\"", StringComparison.Ordinal);
        Assert.True(d < l && l < p);
    }
}
