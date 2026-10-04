using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Vessel3.Server;
using Vessel3.Server.Telemetry;
using Xunit;

namespace Vessel3.Tests;

[Collection(nameof(MetricsTests))]
public class RequestTelemetryTests
{
    private readonly MetricsService metrics = new();

    private sealed class CapturingLogger : ILogger<RequestTelemetry>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private (RequestTelemetry Middleware, CapturingLogger Log) Build(int slowMs, bool accessLog = false)
    {
        var log = new CapturingLogger();
        return (new RequestTelemetry(new RequestTelemetryOptions(TimeSpan.FromMilliseconds(slowMs), accessLog), log, metrics), log);
    }

    private static DefaultHttpContext Context()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentLength = 42;
        return ctx;
    }

    private string Rendered()
    {
        var sb = new StringBuilder();
        metrics.Render(sb, []);
        return sb.ToString();
    }

    [Fact]
    public async Task LogRequest_FastSuccess_CountsWithoutLogging()
    {
        var (mw, log) = Build(slowMs: 1000);

        await mw.InvokeAsync(Context(), ctx =>
        {
            RequestTrace.Current!.Action = "PutObject";
            RequestTrace.Current.Bucket = "b";
            RequestTrace.Current.Add(Stage.LogSync, Stopwatch.Frequency / 1000);
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        Assert.Empty(log.Entries);
        var text = Rendered();
        Assert.Contains("vessel3_requests_total{action=\"PutObject\",status=\"2xx\"} 1", text);
        Assert.Contains("vessel3_request_bytes_total{action=\"PutObject\"} 42", text);
        Assert.Contains("vessel3_stage_duration_seconds_count{stage=\"log_sync\"} 1", text);
        Assert.Null(RequestTrace.Current);
    }

    [Fact]
    public async Task LogRequest_SlowRequest_LogsWarningWithBreakdown()
    {
        var (mw, log) = Build(slowMs: 10);

        await mw.InvokeAsync(Context(), async ctx =>
        {
            RequestTrace.Current!.Action = "ListObjects";
            RequestTrace.Current.Bucket = "loki-chunks";
            RequestTrace.Current.Add(Stage.ReadLock, Stopwatch.Frequency / 100);
            RequestTrace.Current.Add(Stage.Query, Stopwatch.Frequency / 50);
            await Task.Delay(30, TestContext.Current.CancellationToken);
            ctx.Response.StatusCode = 200;
        });

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("action=ListObjects", entry.Message);
        Assert.Contains("bucket=loki-chunks", entry.Message);
        Assert.Contains("status=200", entry.Message);
        Assert.Contains("read_lock_ms=10.0", entry.Message);
        Assert.Contains("query_ms=20.0", entry.Message);
        Assert.Contains("req_bytes=42", entry.Message);
        Assert.Matches(@"total_ms=\d+\.\d", entry.Message);
    }

    [Fact]
    public async Task LogRequest_ServerErrorStatus_LogsEvenWhenFast()
    {
        var (mw, log) = Build(slowMs: 1000);

        await mw.InvokeAsync(Context(), ctx =>
        {
            RequestTrace.Current!.Action = "CompleteMultipartUpload";
            ctx.Response.StatusCode = 500;
            return Task.CompletedTask;
        });

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("status=500", entry.Message);
        Assert.Contains("vessel3_requests_total{action=\"CompleteMultipartUpload\",status=\"5xx\"} 1", Rendered());
    }

    [Fact]
    public async Task LogRequest_UnhandledException_LogsCountsAndRethrows()
    {
        var (mw, log) = Build(slowMs: 1000);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => mw.InvokeAsync(Context(), _ =>
        {
            RequestTrace.Current!.Action = "CompleteMultipartUpload";
            RequestTrace.Current.Key = "big.bin";
            throw new InvalidOperationException("Synchronous operations are disallowed.");
        }));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(ex, entry.Exception);
        Assert.Contains("key=big.bin", entry.Message);
        Assert.Contains("status=500", entry.Message);
        Assert.Contains("vessel3_requests_total{action=\"CompleteMultipartUpload\",status=\"5xx\"} 1", Rendered());
        Assert.Null(RequestTrace.Current);
    }

    [Fact]
    public async Task LogRequest_ClientAbort_DoesNotTreatAsFailure()
    {
        var (mw, log) = Build(slowMs: 1000);
        var ctx = Context();
        using var aborted = new CancellationTokenSource();
        ctx.RequestAborted = aborted.Token;
        aborted.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => mw.InvokeAsync(ctx, _ =>
        {
            RequestTrace.Current!.Action = "GetObject";
            throw new OperationCanceledException();
        }));

        Assert.Empty(log.Entries);
        Assert.Contains("vessel3_requests_total{action=\"GetObject\",status=\"4xx\"} 1", Rendered());
    }

    [Fact]
    public async Task LogRequest_ZeroThreshold_DisablesSlowLogging()
    {
        var (mw, log) = Build(slowMs: 0);

        await mw.InvokeAsync(Context(), async ctx =>
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            ctx.Response.StatusCode = 200;
        });

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task LogRequest_UnroutedRequest_CountsAsOther()
    {
        var (mw, _) = Build(slowMs: 1000);
        await mw.InvokeAsync(Context(), ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; });
        Assert.Contains("vessel3_requests_total{action=\"Other\",status=\"4xx\"} 1", Rendered());
    }

    [Fact]
    public async Task LogRequest_BadHttpRequest_CountsAs4xxWithout500Log()
    {
        var (mw, log) = Build(slowMs: 1000);

        await Assert.ThrowsAsync<BadHttpRequestException>(() => mw.InvokeAsync(Context(), _ =>
        {
            RequestTrace.Current!.Action = "PutObject";
            RequestTrace.Current.Bucket = "skycam";
            RequestTrace.Current.Key = "manifest.jsonl";
            throw new BadHttpRequestException("Unexpected end of request content.", StatusCodes.Status400BadRequest);
        }));

        Assert.Empty(log.Entries);
        var text = Rendered();
        Assert.Contains("vessel3_requests_total{action=\"PutObject\",status=\"4xx\"} 1", text);
        Assert.DoesNotContain("status=\"5xx\"", text);
        Assert.Null(RequestTrace.Current);
    }

    [Fact]
    public async Task LogRequest_SlowNetworkBody_DoesNotTriggerSlowServerWarning()
    {
        var (mw, log) = Build(slowMs: 20);

        await mw.InvokeAsync(Context(), async ctx =>
        {
            RequestTrace.Current!.Action = "PutObject";
            RequestTrace.Current.Bucket = "skycam";
            await Task.Delay(35, TestContext.Current.CancellationToken);
            RequestTrace.Current.Add(Stage.Body, Stopwatch.Frequency * 35 / 1000);
            ctx.Response.StatusCode = 200;
        });

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task LogAccess_Enabled_EmitsInformationAccessLogWithFullContext()
    {
        var (mw, log) = Build(slowMs: 1000, accessLog: true);
        var ctx = Context();
        ctx.Request.Method = "PUT";
        ctx.Request.Path = "/photos/vacation.jpg";
        ctx.Request.Headers["x-request-id"] = "req-custom-12345";
        ctx.Items["CallerIdentity"] = new CallerIdentity("usr_1", "alice", UserRole.Admin, "AK1");

        await mw.InvokeAsync(ctx, innerCtx =>
        {
            RequestTrace.SetContext(protocol: "s3");
            RequestTrace.Current!.Action = "PutObject";
            RequestTrace.Current.Bucket = "photos";
            RequestTrace.Current.Key = "vacation.jpg";
            innerCtx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("HTTP PUT /photos/vacation.jpg -> 200", entry.Message);
        Assert.Contains("action=PutObject", entry.Message);
        Assert.Contains("proto=s3", entry.Message);
        Assert.Contains("bucket=photos", entry.Message);
        Assert.Contains("key=vacation.jpg", entry.Message);
        Assert.Contains("actor=alice", entry.Message);
        Assert.Contains("id=req-custom-12345", entry.Message);
        Assert.Contains("in=42", entry.Message);
    }

    [Fact]
    public async Task LogAccess_W3CTraceparent_ExtractsTraceId()
    {
        var (mw, log) = Build(slowMs: 1000, accessLog: true);
        var ctx = Context();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/buckets";
        ctx.Request.Headers["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        await mw.InvokeAsync(ctx, innerCtx =>
        {
            RequestTrace.Current!.Action = "ListBuckets";
            innerCtx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        var entry = Assert.Single(log.Entries);
        Assert.Contains("id=4bf92f3577b34da6a3ce929d0e0e4736", entry.Message);
    }

    [Fact]
    public async Task LogAccess_ClaimsIdentityUser_ResolvesActor()
    {
        var (mw, log) = Build(slowMs: 1000, accessLog: true);
        var ctx = Context();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/v1/buckets";
        ctx.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "bob")], "test"));

        await mw.InvokeAsync(ctx, innerCtx =>
        {
            RequestTrace.SetContext(protocol: "native");
            RequestTrace.Current!.Action = "ListBuckets";
            innerCtx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        var entry = Assert.Single(log.Entries);
        Assert.Contains("actor=bob", entry.Message);
        Assert.Contains("proto=native", entry.Message);
    }
}
