using System.Diagnostics;

namespace Vessel3.Primitives;

internal enum Stage { GateWait, Body, BlobSync, WriteLock, LogSync, IndexCommit, ReadLock, Query }

internal sealed class RequestTrace
{
    private static readonly AsyncLocal<RequestTrace?> current = new();
    private readonly long[] ticks = new long[8];

    public static RequestTrace? Current { get => current.Value; set => current.Value = value; }

    public long StartedAt { get; } = Stopwatch.GetTimestamp();
    public string TraceId { get; set; } = string.Empty;
    public string Protocol { get; set; } = "http";
    public string Actor { get; set; } = "anonymous";
    public string Action { get; set; } = "Other";
    public string? Bucket { get; set; }
    public string? Key { get; set; }
    public long HandledTicks { get; private set; }

    public void MarkHandled() => HandledTicks = Stopwatch.GetTimestamp() - StartedAt;
    public static void SetAction(string action)
    {
        if (current.Value is { } trace) trace.Action = action;
    }
    public static void SetTarget(string action, string? bucket = null, string? key = null)
    {
        if (current.Value is not { } trace) return;
        trace.Action = action;
        trace.Bucket = bucket;
        trace.Key = key;
    }
    public static void SetContext(string? protocol = null, string? actor = null, string? traceId = null)
    {
        if (current.Value is not { } trace) return;
        if (protocol is not null) trace.Protocol = protocol;
        if (actor is not null) trace.Actor = actor;
        if (traceId is not null) trace.TraceId = traceId;
    }
    public long Ticks(Stage stage) => Interlocked.Read(ref ticks[(int)stage]);
    public void Add(Stage stage, long elapsed) => Interlocked.Add(ref ticks[(int)stage], elapsed);

    public static StageScope Time(Stage stage) => new(current.Value, stage);
    public static void Since(Stage stage, long start) => current.Value?.Add(stage, Stopwatch.GetTimestamp() - start);

    public readonly struct StageScope(RequestTrace? trace, Stage stage) : IDisposable
    {
        private readonly long start = Stopwatch.GetTimestamp();
        public void Dispose() => trace?.Add(stage, Stopwatch.GetTimestamp() - start);
    }
}
