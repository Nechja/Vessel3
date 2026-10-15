namespace Vessel3.Storage;

internal abstract class WriteWorkItem : IDisposable
{
    private readonly ManualResetEventSlim signal = new(false);
    public RequestTrace? Trace { get; } = RequestTrace.Current;
    public Exception? Error { get; private set; }
    public bool PromotedToLeader { get; set; }
    public bool IsCompleted { get; private set; }
    public abstract string? Key { get; }

    public void Wait() => signal.Wait();

    public void Complete(Exception? error = null)
    {
        if (IsCompleted) return;
        IsCompleted = true;
        if (error is not null) Error = error;
        signal.Set();
    }

    public void SignalPromotion() => signal.Set();
    public void ResetSignal() => signal.Reset();
    public void Dispose() => signal.Dispose();

    public abstract bool Prepare(Bucket bucket, List<VersionEvent> batchEvents, out int eventCount);
    public abstract void OnCommitted(Bucket bucket, IReadOnlyList<VersionEvent> appliedEvents, int startIndex, int count);
}

internal abstract class WriteWorkItem<T> : WriteWorkItem
{
    public T Result { get; protected set; } = default!;
}
