using System.Text;
using Vessel3.Server;
using Xunit;

namespace Vessel3.Tests;

[Collection(nameof(MetricsTests))]
[CollectionDefinition(nameof(MetricsTests), DisableParallelization = true)]
public class MetricsTests
{
    public MetricsTests() => Metrics.ResetForTests();

    [Fact]
    public void MethodIndex_KnownVerbs()
    {
        Assert.Equal(0, Metrics.MethodIndex("GET"));
        Assert.Equal(1, Metrics.MethodIndex("PUT"));
        Assert.Equal(2, Metrics.MethodIndex("POST"));
        Assert.Equal(3, Metrics.MethodIndex("DELETE"));
        Assert.Equal(4, Metrics.MethodIndex("HEAD"));
        Assert.Equal(5, Metrics.MethodIndex("PATCH"));
        Assert.Equal(5, Metrics.MethodIndex("get"));
    }

    [Fact]
    public void StatusIndex_Classes()
    {
        Assert.Equal(0, Metrics.StatusIndex(200));
        Assert.Equal(0, Metrics.StatusIndex(204));
        Assert.Equal(1, Metrics.StatusIndex(304));
        Assert.Equal(2, Metrics.StatusIndex(403));
        Assert.Equal(3, Metrics.StatusIndex(500));
        Assert.Equal(4, Metrics.StatusIndex(0));
        Assert.Equal(4, Metrics.StatusIndex(700));
    }

    [Fact]
    public void Render_EmitsExpectedSeriesAfterRecording()
    {
        Metrics.RecordRequest(Metrics.MethodIndex("PUT"), Metrics.StatusIndex(200), elapsedTicks: 0, reqBytes: 123, resBytes: 0);
        Metrics.RecordRequest(Metrics.MethodIndex("GET"), Metrics.StatusIndex(404), elapsedTicks: 0, reqBytes: 0, resBytes: 17);

        var sb = new StringBuilder();
        Metrics.Render(sb);
        var text = sb.ToString();

        Assert.Contains("# TYPE vessel3_http_requests_total counter", text);
        Assert.Contains("vessel3_http_requests_total{method=\"PUT\",status=\"2xx\"} 1", text);
        Assert.Contains("vessel3_http_requests_total{method=\"GET\",status=\"4xx\"} 1", text);
        Assert.Contains("vessel3_http_request_bytes_total{method=\"PUT\"} 123", text);
        Assert.Contains("vessel3_http_response_bytes_total{method=\"GET\"} 17", text);
        Assert.Contains("# TYPE vessel3_http_request_duration_seconds histogram", text);
        Assert.Contains("vessel3_http_request_duration_seconds_count{method=\"PUT\"} 1", text);
        Assert.Contains("vessel3_http_request_duration_seconds_bucket{method=\"PUT\",le=\"+Inf\"} 1", text);
        Assert.Contains("# TYPE process_resident_memory_bytes gauge", text);
        Assert.Contains("dotnet_gc_collections_total{generation=\"0\"}", text);
    }

    [Fact]
    public void Render_OmitsZeroSeriesForUnusedMethods()
    {
        var text = new StringBuilder();
        Metrics.Render(text);
        var rendered = text.ToString();
        Assert.DoesNotContain("vessel3_http_requests_total{method=\"GET\"", rendered);
        Assert.DoesNotContain("vessel3_http_request_duration_seconds_count{method=\"GET\"}", rendered);
    }

    [Fact]
    public void Histogram_BucketsAreCumulative()
    {
        // 0 ticks -> 0 seconds, lands in smallest bucket; all higher buckets must also see it.
        Metrics.RecordRequest(Metrics.MethodIndex("GET"), Metrics.StatusIndex(200), elapsedTicks: 0, reqBytes: 0, resBytes: 0);
        var sb = new StringBuilder();
        Metrics.Render(sb);
        var text = sb.ToString();
        Assert.Contains("vessel3_http_request_duration_seconds_bucket{method=\"GET\",le=\"0.001\"} 1", text);
        Assert.Contains("vessel3_http_request_duration_seconds_bucket{method=\"GET\",le=\"10\"} 1", text);
        Assert.Contains("vessel3_http_request_duration_seconds_bucket{method=\"GET\",le=\"+Inf\"} 1", text);
    }
}
