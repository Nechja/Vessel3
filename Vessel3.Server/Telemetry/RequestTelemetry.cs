using System.Diagnostics;
using Vessel3.Server.Telemetry;

namespace Vessel3.Server;

internal sealed record RequestTelemetryOptions(TimeSpan SlowThreshold, bool AccessLogEnabled = false);

internal sealed partial class RequestTelemetry(
    RequestTelemetryOptions options,
    ILogger<RequestTelemetry> log,
    IMetricsCollector metrics,
    IServerLogBuffer? logBuffer = null) : IMiddleware
{
    public static readonly ActivitySource ActivitySource = new("Vessel3", "1.0.0");

    private readonly long slowTicks = options.SlowThreshold > TimeSpan.Zero
        ? (long)(options.SlowThreshold.TotalSeconds * Stopwatch.Frequency)
        : long.MaxValue;

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        var parentContext = ExtractParentContext(ctx);
        using var activity = StartActivity(parentContext, ctx);
        var trace = InitializeTrace(activity, ctx);

        Exception? failure = null;
        try
        {
            await next(ctx);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            FinalizeRequest(ctx, trace, activity, failure);
        }
    }

    private void FinalizeRequest(HttpContext ctx, RequestTrace trace, Activity? activity, Exception? failure)
    {
        RequestTrace.Current = null;
        var elapsed = Stopwatch.GetTimestamp() - trace.StartedAt;
        var aborted = ctx.RequestAborted.IsCancellationRequested || failure is OperationCanceledException;
        var status = ResolveStatusCode(ctx, failure, aborted);
        var reqBytes = ctx.Request.ContentLength ?? 0;
        var resBytes = ctx.Response.ContentLength ?? 0;

        ResolveActor(ctx, trace);
        EnrichActivity(activity, trace, status, failure, ctx);
        RecordMetrics(trace, status, elapsed, reqBytes, resBytes);
        LogToBuffer(ctx, trace, status, failure, elapsed);
        LogAccessIfEnabled(ctx, trace, status, elapsed, reqBytes, resBytes);
        LogSlowOrError(ctx, trace, status, failure, elapsed, aborted, reqBytes, resBytes);
    }

    private static Activity? StartActivity(ActivityContext parentContext, HttpContext ctx)
    {
        var activity = parentContext != default
            ? ActivitySource.StartActivity("vessel3.request", ActivityKind.Server, parentContext)
            : ActivitySource.StartActivity("vessel3.request", ActivityKind.Server);

        if (activity is not null)
        {
            activity.SetTag("rpc.system", "vessel3");
            activity.SetTag("http.request.method", ctx.Request.Method);
            activity.SetTag("server.address", ctx.Request.Host.Host);
            activity.SetTag("url.path", ctx.Request.Path.Value ?? "/");
        }

        return activity;
    }

    private static RequestTrace InitializeTrace(Activity? activity, HttpContext ctx)
    {
        var trace = new RequestTrace
        {
            TraceId = activity?.TraceId.ToHexString() ?? ResolveTraceId(ctx)
        };
        RequestTrace.Current = trace;
        return trace;
    }

    private static int ResolveStatusCode(HttpContext ctx, Exception? failure, bool aborted) => failure switch
    {
        null => ctx.Response.StatusCode,
        _ when aborted => 499,
        BadHttpRequestException badHttp => badHttp.StatusCode,
        _ => 500,
    };

    private static void ResolveActor(HttpContext ctx, RequestTrace trace)
    {
        if (!string.Equals(trace.Actor, "anonymous", StringComparison.Ordinal))
        {
            return;
        }

        if (ctx.Items.TryGetValue("CallerIdentity", out var obj) && obj is CallerIdentity caller)
        {
            trace.Actor = caller.Username;
        }
        else if (!string.IsNullOrEmpty(ctx.User.Identity?.Name))
        {
            trace.Actor = ctx.User.Identity.Name;
        }
    }

    private static void EnrichActivity(Activity? activity, RequestTrace trace, int status, Exception? failure, HttpContext ctx)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("vessel3.protocol", trace.Protocol);
        activity.SetTag("vessel3.action", trace.Action);
        if (!string.IsNullOrEmpty(trace.Bucket))
        {
            activity.SetTag("vessel3.bucket", trace.Bucket);
        }
        if (!string.IsNullOrEmpty(trace.Key))
        {
            activity.SetTag("vessel3.key", trace.Key);
        }
        activity.SetTag("vessel3.actor", trace.Actor);
        activity.SetTag("http.response.status_code", status);

        if (failure is not null || status >= 500)
        {
            activity.SetStatus(ActivityStatusCode.Error, failure?.Message ?? $"HTTP {status}");
            if (failure is not null)
            {
                activity.AddException(failure);
            }
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }

        if (!ctx.Response.HasStarted)
        {
            ctx.Response.Headers["traceparent"] = $"00-{activity.TraceId.ToHexString()}-{activity.SpanId.ToHexString()}-01";
        }
    }

    private void RecordMetrics(RequestTrace trace, int status, long elapsed, long reqBytes, long resBytes)
    {
        metrics.RecordRequest(trace.Action, status, elapsed, reqBytes, resBytes);
        metrics.RecordStages(trace);
    }

    private void LogToBuffer(HttpContext ctx, RequestTrace trace, int status, Exception? failure, long elapsed)
    {
        if (logBuffer is null)
        {
            return;
        }

        var totalMs = Ms(elapsed);
        var subject = !string.IsNullOrEmpty(trace.Bucket)
            ? (!string.IsNullOrEmpty(trace.Key) ? $"{trace.Bucket}/{trace.Key}" : trace.Bucket)
            : null;
        var level = status switch
        {
            >= 500 => "Error",
            >= 400 => "Warning",
            _ => "Information"
        };

        logBuffer.Log(new ServerLogEntry(
            Id: Guid.NewGuid().ToString("N"),
            Timestamp: DateTimeOffset.UtcNow,
            Level: level,
            Source: "Access",
            Message: $"{ctx.Request.Method} {ctx.Request.Path.Value ?? "/"} -> {status}",
            Protocol: trace.Protocol,
            Action: trace.Action,
            Subject: subject,
            Actor: trace.Actor,
            StatusCode: status,
            DurationMs: totalMs,
            TraceId: trace.TraceId,
            ErrorDetails: failure?.ToString()));
    }

    private void LogAccessIfEnabled(HttpContext ctx, RequestTrace trace, int status, long elapsed, long reqBytes, long resBytes)
    {
        if (!options.AccessLogEnabled || !log.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var totalMs = Ms(elapsed);
        var path = ctx.Request.Path.Value ?? "/";
        LogAccess(log, ctx.Request.Method, path, status, totalMs,
            trace.Action, trace.Protocol, trace.Bucket, trace.Key, trace.Actor, reqBytes, resBytes, trace.TraceId);
    }

    private void LogSlowOrError(HttpContext ctx, RequestTrace trace, int status, Exception? failure, long elapsed, bool aborted, long reqBytes, long resBytes)
    {
        var serverTicks = trace.HandledTicks > 0
            ? Math.Max(0, trace.HandledTicks - trace.Ticks(Stage.Body))
            : Math.Max(0, elapsed - trace.Ticks(Stage.Body));
        if (status < 500 && serverTicks < slowTicks)
        {
            return;
        }

        var level = status >= 500 ? LogLevel.Error : LogLevel.Warning;
        var totalMs = Ms(elapsed);
        var handlerMs = Ms(trace.HandledTicks);
        var gateWaitMs = Ms(trace.Ticks(Stage.GateWait));
        var bodyMs = Ms(trace.Ticks(Stage.Body));
        var blobSyncMs = Ms(trace.Ticks(Stage.BlobSync));
        var writeLockMs = Ms(trace.Ticks(Stage.WriteLock));
        var logSyncMs = Ms(trace.Ticks(Stage.LogSync));
        var indexCommitMs = Ms(trace.Ticks(Stage.IndexCommit));
        var readLockMs = Ms(trace.Ticks(Stage.ReadLock));
        var queryMs = Ms(trace.Ticks(Stage.Query));

        LogRequest(log, level, aborted ? null : failure, trace.Action, trace.Protocol, trace.Bucket, trace.Key, trace.Actor, trace.TraceId,
            status, totalMs, handlerMs, gateWaitMs, bodyMs, blobSyncMs, writeLockMs, logSyncMs, indexCommitMs, readLockMs, queryMs,
            reqBytes, resBytes);
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static ActivityContext ExtractParentContext(HttpContext ctx)
    {
        var headers = ctx.Request.Headers;
        if (!headers.TryGetValue("traceparent", out var traceParentHeader) || string.IsNullOrEmpty(traceParentHeader))
        {
            return default;
        }

        var traceParentValue = traceParentHeader.ToString();
        var parts = traceParentValue.Split('-');
        if (parts.Length < 4 || parts[1].Length != 32 || parts[2].Length != 16)
        {
            return default;
        }

        try
        {
            var traceId = ActivityTraceId.CreateFromString(parts[1].AsSpan());
            var spanId = ActivitySpanId.CreateFromString(parts[2].AsSpan());
            var flags = parts[3] == "01" ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None;
            return new ActivityContext(traceId, spanId, flags, isRemote: true);
        }
        catch
        {
            return default;
        }
    }

    private static string ResolveTraceId(HttpContext ctx)
    {
        var headers = ctx.Request.Headers;
        if (headers.TryGetValue("traceparent", out var traceParentHeader) && !string.IsNullOrEmpty(traceParentHeader))
        {
            var traceParentValue = traceParentHeader.ToString();
            var parts = traceParentValue.Split('-');
            return parts.Length >= 4 && parts[1].Length == 32 ? parts[1] : traceParentValue;
        }

        if (headers.TryGetValue("x-request-id", out var requestId) && !string.IsNullOrEmpty(requestId))
            return requestId.ToString();

        if (headers.TryGetValue("x-amz-request-id", out var amzRequestId) && !string.IsNullOrEmpty(amzRequestId))
            return amzRequestId.ToString();

        if (headers.TryGetValue("x-ms-request-id", out var msRequestId) && !string.IsNullOrEmpty(msRequestId))
            return msRequestId.ToString();

        return !string.IsNullOrEmpty(ctx.TraceIdentifier) ? ctx.TraceIdentifier : Ulid.NewUlid().ToString();
    }

    [LoggerMessage(EventId = 1, Message =
        "request: action={Action} proto={Protocol} bucket={Bucket} key={Key} actor={Actor} id={TraceId} status={Status} total_ms={TotalMs:F1} handler_ms={HandlerMs:F1} " +
        "gate_wait_ms={GateWaitMs:F1} body_ms={BodyMs:F1} blob_sync_ms={BlobSyncMs:F1} write_lock_ms={WriteLockMs:F1} " +
        "log_sync_ms={LogSyncMs:F1} index_commit_ms={IndexCommitMs:F1} read_lock_ms={ReadLockMs:F1} query_ms={QueryMs:F1} " +
        "req_bytes={ReqBytes} res_bytes={ResBytes}")]
    private static partial void LogRequest(ILogger logger, LogLevel level, Exception? exception,
        string action, string protocol, string? bucket, string? key, string actor, string traceId, int status, double totalMs, double handlerMs,
        double gateWaitMs, double bodyMs, double blobSyncMs, double writeLockMs, double logSyncMs, double indexCommitMs,
        double readLockMs, double queryMs, long reqBytes, long resBytes);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message =
        "HTTP {Method} {Path} -> {Status} in {TotalMs:F1}ms [action={Action} proto={Protocol} bucket={Bucket} key={Key} actor={Actor} in={ReqBytes} out={ResBytes} id={TraceId}]")]
    private static partial void LogAccess(ILogger logger, string method, string path, int status, double totalMs,
        string action, string protocol, string? bucket, string? key, string actor, long reqBytes, long resBytes, string traceId);
}
