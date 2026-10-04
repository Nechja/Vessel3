using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Vessel3.Server;
using Vessel3.Server.Configuration;
using Vessel3.Server.Telemetry;
using Vessel3.Server.Telemetry.Otlp;
using Xunit;

namespace Vessel3.Tests;

public sealed class OpenTelemetryTracingTests
{
    private sealed class DummyMetricsCollector : IMetricsCollector
    {
        public void RecordRequest(string action, int statusCode, long elapsedTicks, long requestBytes, long responseBytes) { }
        public void RecordStages(RequestTrace trace) { }
    }

    [Fact]
    public async Task RequestTelemetry_StartsActivity_AndEnrichesTags()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Vessel3",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => captured = a
        };
        ActivitySource.AddActivityListener(listener);

        var middleware = new RequestTelemetry(
            new RequestTelemetryOptions(TimeSpan.Zero, AccessLogEnabled: false),
            NullLogger<RequestTelemetry>.Instance,
            new DummyMetricsCollector());

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "PUT";
        ctx.Request.Host = new HostString("localhost", 9000);
        ctx.Request.Path = "/test-bucket/my-file.txt";

        await middleware.InvokeAsync(ctx, innerCtx =>
        {
            if (RequestTrace.Current is { } trace)
            {
                trace.Protocol = "s3";
                trace.Action = "PutObject";
                trace.Bucket = "test-bucket";
                trace.Key = "my-file.txt";
                trace.Actor = "test-user";
            }
            innerCtx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        Assert.NotNull(captured);
        Assert.Equal("vessel3.request", captured.DisplayName);
        Assert.Equal(ActivityStatusCode.Ok, captured.Status);

        var tags = captured.TagObjects.ToDictionary(t => t.Key, t => t.Value?.ToString());
        Assert.Equal("vessel3", tags["rpc.system"]);
        Assert.Equal("PUT", tags["http.request.method"]);
        Assert.Equal("s3", tags["vessel3.protocol"]);
        Assert.Equal("PutObject", tags["vessel3.action"]);
        Assert.Equal("test-bucket", tags["vessel3.bucket"]);
        Assert.Equal("my-file.txt", tags["vessel3.key"]);
        Assert.Equal("test-user", tags["vessel3.actor"]);
        Assert.Equal("200", tags["http.response.status_code"]);
        Assert.True(ctx.Response.Headers.ContainsKey("traceparent"));
    }

    [Fact]
    public async Task RequestTelemetry_ExtractsParentTraceContext_FromW3CTraceparent()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Vessel3",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => captured = a
        };
        ActivitySource.AddActivityListener(listener);

        var middleware = new RequestTelemetry(
            new RequestTelemetryOptions(TimeSpan.Zero, AccessLogEnabled: false),
            NullLogger<RequestTelemetry>.Instance,
            new DummyMetricsCollector());

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        const string expectedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        const string expectedParentSpanId = "00f067aa0ba902b7";
        ctx.Request.Headers["traceparent"] = $"00-{expectedTraceId}-{expectedParentSpanId}-01";

        await middleware.InvokeAsync(ctx, innerCtx =>
        {
            innerCtx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        Assert.NotNull(captured);
        Assert.Equal(expectedTraceId, captured.TraceId.ToHexString());
        Assert.Equal(expectedParentSpanId, captured.ParentSpanId.ToHexString());
    }

    [Fact]
    public async Task RequestTelemetry_MarksErrorStatus_WhenExceptionThrown()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Vessel3",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => captured = a
        };
        ActivitySource.AddActivityListener(listener);

        var middleware = new RequestTelemetry(
            new RequestTelemetryOptions(TimeSpan.Zero, AccessLogEnabled: false),
            NullLogger<RequestTelemetry>.Instance,
            new DummyMetricsCollector());

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "DELETE";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(ctx, _ => throw new InvalidOperationException("disk failure")));

        Assert.NotNull(captured);
        Assert.Equal(ActivityStatusCode.Error, captured.Status);
        Assert.Contains("disk failure", captured.StatusDescription);
    }

    [Fact]
    public void OtlpTracePayload_SerializesCorrectly()
    {
        var payload = new OtlpTracePayload(
        [
            new OtlpResourceSpans(
                Resource: new OtlpResource(
                [
                    new OtlpKeyValue("service.name", new OtlpAnyValue(StringValue: "vessel3"))
                ]),
                ScopeSpans:
                [
                    new OtlpScopeSpans(
                        Scope: new OtlpScope("Vessel3", "1.0.0"),
                        Spans:
                        [
                            new OtlpSpan(
                                TraceId: "4bf92f3577b34da6a3ce929d0e0e4736",
                                SpanId: "00f067aa0ba902b7",
                                ParentSpanId: null,
                                Name: "vessel3.request",
                                Kind: 2,
                                StartTimeUnixNano: "1728000000000000000",
                                EndTimeUnixNano: "1728000000050000000",
                                Attributes:
                                [
                                    new OtlpKeyValue("vessel3.protocol", new OtlpAnyValue(StringValue: "s3")),
                                    new OtlpKeyValue("http.response.status_code", new OtlpAnyValue(IntValue: "200"))
                                ],
                                Status: new OtlpStatus(1, null))
                        ])
                ])
        ]);

        var json = JsonSerializer.Serialize(payload, OtlpJsonContext.Default.OtlpTracePayload);
        Assert.NotEmpty(json);
        Assert.Contains("\"service.name\"", json);
        Assert.Contains("\"4bf92f3577b34da6a3ce929d0e0e4736\"", json);
        Assert.Contains("\"vessel3.protocol\"", json);
        Assert.Contains("\"vessel3.request\"", json);
    }
}
