namespace Vessel3.Storage;

internal interface IGcGate
{
    Task<IDisposable> Writing();
    Task<IDisposable?> Collecting(TimeSpan maxWait);
}

internal sealed class GcGate : IGcGate, IDisposable
{
    public void Dispose() => collectorTurnstile.Dispose();

    private readonly SemaphoreSlim collectorTurnstile = new(1, 1);
    private readonly Lock sync = new();
    private readonly Queue<TaskCompletionSource> queuedWriters = new();
    private int activeWriters;
    private bool collecting;
    private TaskCompletionSource? writersDrained;

    public Task<IDisposable> Writing()
    {
        TaskCompletionSource queued;
        lock (sync)
        {
            if (!collecting)
            {
                activeWriters++;
                return Task.FromResult<IDisposable>(new WriteLease(this));
            }
            queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queuedWriters.Enqueue(queued);
        }
        return WaitForWriteTurn(queued);
    }

    private async Task<IDisposable> WaitForWriteTurn(TaskCompletionSource queued)
    {
        using (RequestTrace.Time(Stage.GateWait)) await queued.Task;
        return new WriteLease(this);
    }

    public async Task<IDisposable?> Collecting(TimeSpan maxWait)
    {
        using var deadline = new CancellationTokenSource(maxWait);
        try
        {
            await collectorTurnstile.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        Task? drain = null;
        lock (sync)
        {
            collecting = true;
            if (activeWriters > 0)
            {
                writersDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drain = writersDrained.Task;
            }
        }

        if (drain is not null)
        {
            try
            {
                await drain.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                ReleaseCollecting();
                return null;
            }
        }

        return new CollectLease(this);
    }

    private void ReleaseWriting()
    {
        TaskCompletionSource? drained = null;
        lock (sync)
        {
            activeWriters--;
            if (collecting && activeWriters == 0)
            {
                drained = writersDrained;
                writersDrained = null;
            }
        }
        drained?.TrySetResult();
    }

    private void ReleaseCollecting()
    {
        var admitted = new List<TaskCompletionSource>();
        lock (sync)
        {
            collecting = false;
            writersDrained = null;
            while (queuedWriters.TryDequeue(out var queued))
            {
                activeWriters++;
                admitted.Add(queued);
            }
        }
        foreach (var queued in admitted) queued.TrySetResult();
        collectorTurnstile.Release();
    }

    private sealed class WriteLease(GcGate gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) gate.ReleaseWriting();
        }
    }

    private sealed class CollectLease(GcGate gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) gate.ReleaseCollecting();
        }
    }
}
