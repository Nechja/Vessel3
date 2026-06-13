using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace Vessel3.Server.Storage;

internal interface IDurabilityWriter
{
    Task SyncData(SafeFileHandle handle);
    Task SyncDirectory(string dirPath);
}

// Offloads blocking fdatasync/fsync off request threads onto a fixed set of dedicated I/O
// threads. Callers await; the request thread is freed while a dedicated thread runs the
// syscall. Several threads let the block layer coalesce concurrent flushes.
internal sealed class DurabilityWriter(int threads) : IDurabilityWriter, IHostedService, IDisposable
{
    private readonly BlockingCollection<WorkItem> queue = [];
    private readonly List<Thread> workers = [];

    private readonly record struct WorkItem(Action Op, TaskCompletionSource Tcs);

    public Task SyncData(SafeFileHandle handle) =>
        PosixFsync.IsLinux ? Enqueue(() => PosixFsync.DataSync(handle)) : Task.CompletedTask;

    public Task SyncDirectory(string dirPath) =>
        PosixFsync.IsLinux ? Enqueue(() => PosixFsync.SyncDirectory(dirPath)) : Task.CompletedTask;

    private Task Enqueue(Action op)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { queue.Add(new WorkItem(op, tcs)); }
        catch (InvalidOperationException) { tcs.SetException(new ObjectDisposedException(nameof(DurabilityWriter))); }
        return tcs.Task;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (!PosixFsync.IsLinux) return Task.CompletedTask;
        for (var i = 0; i < threads; i++)
        {
            var t = new Thread(Drain) { IsBackground = true, Name = $"vessel3-fsync-{i}" };
            t.Start();
            workers.Add(t);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        queue.CompleteAdding();
        foreach (var t in workers) t.Join();
        return Task.CompletedTask;
    }

    public void Dispose() => queue.Dispose();

    private void Drain()
    {
        foreach (var item in queue.GetConsumingEnumerable())
        {
            try { item.Op(); item.Tcs.SetResult(); }
            catch (Exception ex) { item.Tcs.TrySetException(ex); }
        }
    }
}
