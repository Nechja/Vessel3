using System.Diagnostics;

namespace Vessel3.Server;

internal enum Stage { GateWait, Body, BlobSync, WriteLock, LogSync, IndexCommit, ReadLock, Query }

internal sealed class RequestTrace
{
    private static readonly AsyncLocal<RequestTrace?> current = new();
    private readonly long[] ticks = new long[8];

    public static RequestTrace? Current { get => current.Value; set => current.Value = value; }

    public long StartedAt { get; } = Stopwatch.GetTimestamp();
    public string Action { get; set; } = "Other";
    public string? Bucket { get; set; }
    public string? Key { get; set; }
    public long HandledTicks { get; private set; }

    public void MarkHandled() => HandledTicks = Stopwatch.GetTimestamp() - StartedAt;
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
