using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Vessel3.Storage;

internal sealed class BucketWriteQueue
{
    private readonly Lock queueLock = new();
    private readonly Queue<WriteWorkItem> queue = new();
    private bool isDraining;

    public Lock WriteGate { get; } = new();

    public bool CanSeal(IBucketIndex index)
    {
        lock (queueLock)
        {
            return queue.Count == 0 && index.IsEmpty();
        }
    }

    public T Execute<T>(WriteWorkItem<T> item, Bucket bucket, VersionLog log, IBucketIndex index, long startWait)
    {
        try
        {
            bool isLeader;
            lock (queueLock)
            {
                queue.Enqueue(item);
                if (!isDraining)
                {
                    isDraining = true;
                    isLeader = true;
                }
                else
                {
                    isLeader = false;
                }
            }

            while (true)
            {
                if (isLeader)
                {
                    DrainBatch(bucket, log, index);
                    break;
                }

                item.Wait();

                if (item.PromotedToLeader)
                {
                    item.ResetSignal();
                    item.PromotedToLeader = false;
                    isLeader = true;
                    continue;
                }

                break;
            }

            RequestTrace.Since(Stage.WriteLock, startWait);
            if (item.Error is not null)
            {
                ExceptionDispatchInfo.Throw(item.Error);
            }
            return item.Result;
        }
        finally
        {
            item.Dispose();
        }
    }

    private void DrainBatch(Bucket bucket, VersionLog log, IBucketIndex index)
    {
        List<WriteWorkItem> batch;
        lock (queueLock)
        {
            if (queue.Count == 0)
            {
                isDraining = false;
                return;
            }
            batch = new List<WriteWorkItem>(queue.Count);
            while (queue.Count > 0)
            {
                batch.Add(queue.Dequeue());
            }
        }

        try
        {
            try
            {
                ProcessBatch(batch, bucket, log, index);
            }
            catch (Exception exception)
            {
                foreach (var item in batch)
                {
                    if (!item.IsCompleted)
                    {
                        item.Complete(exception);
                    }
                }
            }
        }
        finally
        {
            lock (queueLock)
            {
                if (queue.Count > 0)
                {
                    var nextLeader = queue.Peek();
                    nextLeader.PromotedToLeader = true;
                    nextLeader.SignalPromotion();
                }
                else
                {
                    isDraining = false;
                }
            }
        }
    }

    private void ProcessBatch(List<WriteWorkItem> batch, Bucket bucket, VersionLog log, IBucketIndex index)
    {
        lock (WriteGate)
        {
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            List<(WriteWorkItem Item, int EventCount)> subBatchItems = [];
            List<VersionEvent> subBatchEvents = [];

            foreach (var item in batch)
            {
                if (HasBatchConflict(item, seenKeys))
                {
                    CommitSubBatch(bucket, log, index, subBatchItems, subBatchEvents);
                    subBatchItems.Clear();
                    subBatchEvents.Clear();
                    seenKeys.Clear();
                }

                if (item.Key is { } key)
                {
                    seenKeys.Add(key);
                }

                if (!item.Prepare(bucket, subBatchEvents, out var eventCount))
                {
                    item.Complete();
                    continue;
                }

                subBatchItems.Add((item, eventCount));
            }

            if (subBatchItems.Count > 0)
            {
                CommitSubBatch(bucket, log, index, subBatchItems, subBatchEvents);
            }
        }
    }

    private static bool HasBatchConflict(WriteWorkItem item, HashSet<string> seenKeys) =>
        item is BatchDeleteWorkItem || (item.Key is { } key && seenKeys.Contains(key));

    private static void CommitSubBatch(
        Bucket bucket,
        VersionLog log,
        IBucketIndex index,
        List<(WriteWorkItem Item, int EventCount)> subBatchItems,
        List<VersionEvent> subBatchEvents)
    {
        if (subBatchEvents.Count == 0)
        {
            foreach (var (item, _) in subBatchItems)
            {
                item.Complete();
            }
            return;
        }

        var logAppendStart = Stopwatch.GetTimestamp();
        var applied = log.Append(subBatchEvents);
        var logDurationTicks = Stopwatch.GetTimestamp() - logAppendStart;

        long indexCommitDurationTicks;
        using (var commit = RequestTrace.Time(Stage.IndexCommit))
        using (var transaction = index.BeginTransaction())
        {
            var indexCommitStart = Stopwatch.GetTimestamp();
            foreach (var operation in applied) operation.ApplyTo(index);
            transaction.Commit();
            indexCommitDurationTicks = Stopwatch.GetTimestamp() - indexCommitStart;
        }

        var eventIndex = 0;
        foreach (var (item, count) in subBatchItems)
        {
            if (count > 0)
            {
                item.OnCommitted(bucket, applied, eventIndex, count);
                eventIndex += count;
            }
            if (item.Trace is { } followerTrace && !ReferenceEquals(followerTrace, RequestTrace.Current))
            {
                followerTrace.Add(Stage.LogSync, logDurationTicks);
                followerTrace.Add(Stage.IndexCommit, indexCommitDurationTicks);
            }
            item.Complete();
        }
    }

    public void FlushDeletes(
        VersionLog log,
        IBucketIndex index,
        List<(int Slot, IReadOnlyList<VersionEvent> Events, DeleteOutcome Outcome)> pending,
        Result<DeleteOutcome>[] results)
    {
        List<VersionEvent> events = [];
        foreach (var (_, versionEvents, _) in pending) events.AddRange(versionEvents);
        if (events.Count > 0)
        {
            var applied = log.Append(events);
            using var commit = RequestTrace.Time(Stage.IndexCommit);
            using var transaction = index.BeginTransaction();
            foreach (var operation in applied) operation.ApplyTo(index);
            transaction.Commit();
        }
        foreach (var (slot, _, outcome) in pending) results[slot] = outcome;
        pending.Clear();
    }
}
