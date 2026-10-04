using System.Diagnostics;
using Vessel3.Server.Telemetry;

namespace Vessel3.Server;

internal sealed record RequestTelemetryOptions(TimeSpan SlowThreshold, bool AccessLogEnabled = false);

internal sealed partial class RequestTelemetry(
    RequestTelemetryOptions options,
    ILogger<RequestTelemetry> log,
    IMetricsCollector metrics) : IMiddleware
{

    private readonly long slowTicks = options.SlowThreshold > TimeSpan.Zero
        ? (long)(options.SlowThreshold.TotalSeconds * Stopwatch.Frequency)
        : long.MaxValue;

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        var trace = new RequestTrace
        {
            TraceId = ResolveTraceId(ctx)
        };
        RequestTrace.Current = trace;
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
            RequestTrace.Current = null;
            var elapsed = Stopwatch.GetTimestamp() - trace.StartedAt;
            var aborted = ctx.RequestAborted.IsCancellationRequested || failure is OperationCanceledException;
            var status = failure switch
            {
                null => ctx.Response.StatusCode,
                _ when aborted => 499,
                BadHttpRequestException badHttp => badHttp.StatusCode,
                _ => 500,
            };
            var reqBytes = ctx.Request.ContentLength ?? 0;
            var resBytes = ctx.Response.ContentLength ?? 0;

            if (string.Equals(trace.Actor, "anonymous", StringComparison.Ordinal))
            {
                if (ctx.Items.TryGetValue("CallerIdentity", out var obj) && obj is CallerIdentity caller)
                {
                    trace.Actor = caller.Username;
                }
                else if (!string.IsNullOrEmpty(ctx.User.Identity?.Name))
                {
                    trace.Actor = ctx.User.Identity.Name;
                }
            }

            metrics.RecordRequest(trace.Action, status, elapsed, reqBytes, resBytes);
            metrics.RecordStages(trace);

            if (options.AccessLogEnabled && log.IsEnabled(LogLevel.Information))
            {
                var totalMs = Ms(elapsed);
                var path = ctx.Request.Path.Value ?? "/";
                LogAccess(log, ctx.Request.Method, path, status, totalMs,
                    trace.Action, trace.Protocol, trace.Bucket, trace.Key, trace.Actor, reqBytes, resBytes, trace.TraceId);
            }

            var serverTicks = Math.Max(0, elapsed - trace.Ticks(Stage.Body));
            if (status >= 500 || serverTicks >= slowTicks)
            {
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
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string ResolveTraceId(HttpContext ctx)
    {
        var headers = ctx.Request.Headers;
        if (headers.TryGetValue("traceparent", out var tp) && !string.IsNullOrEmpty(tp))
        {
            var val = tp.ToString();
            var parts = val.Split('-');
            return parts.Length >= 4 && parts[1].Length == 32 ? parts[1] : val;
        }

        if (headers.TryGetValue("x-request-id", out var xrid) && !string.IsNullOrEmpty(xrid))
            return xrid.ToString();

        if (headers.TryGetValue("x-amz-request-id", out var amzid) && !string.IsNullOrEmpty(amzid))
            return amzid.ToString();

        if (headers.TryGetValue("x-ms-request-id", out var msid) && !string.IsNullOrEmpty(msid))
            return msid.ToString();

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
