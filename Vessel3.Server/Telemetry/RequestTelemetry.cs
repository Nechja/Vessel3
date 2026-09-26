using System.Diagnostics;
using Vessel3.Server.Telemetry;

namespace Vessel3.Server;

internal sealed record RequestTelemetryOptions(TimeSpan SlowThreshold);

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
        var trace = new RequestTrace();
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
            var aborted = failure is OperationCanceledException && ctx.RequestAborted.IsCancellationRequested;
            var status = failure is null ? ctx.Response.StatusCode : aborted ? 499 : 500;
            var reqBytes = ctx.Request.ContentLength ?? 0;
            var resBytes = ctx.Response.ContentLength ?? 0;

            metrics.RecordRequest(trace.Action, status, elapsed, reqBytes, resBytes);
            metrics.RecordStages(trace);

            if (status >= 500 || elapsed >= slowTicks)
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
                LogRequest(log, level, aborted ? null : failure, trace.Action, trace.Bucket, trace.Key, status,
                    totalMs, handlerMs, gateWaitMs, bodyMs, blobSyncMs, writeLockMs, logSyncMs, indexCommitMs, readLockMs, queryMs,
                    reqBytes, resBytes);
            }
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    [LoggerMessage(EventId = 1, Message =
        "request: action={Action} bucket={Bucket} key={Key} status={Status} total_ms={TotalMs:F1} handler_ms={HandlerMs:F1} " +
        "gate_wait_ms={GateWaitMs:F1} body_ms={BodyMs:F1} blob_sync_ms={BlobSyncMs:F1} write_lock_ms={WriteLockMs:F1} " +
        "log_sync_ms={LogSyncMs:F1} index_commit_ms={IndexCommitMs:F1} read_lock_ms={ReadLockMs:F1} query_ms={QueryMs:F1} " +
        "req_bytes={ReqBytes} res_bytes={ResBytes}")]
    private static partial void LogRequest(ILogger logger, LogLevel level, Exception? exception,
        string action, string? bucket, string? key, int status, double totalMs, double handlerMs,
        double gateWaitMs, double bodyMs, double blobSyncMs, double writeLockMs, double logSyncMs, double indexCommitMs,
        double readLockMs, double queryMs, long reqBytes, long resBytes);
}
