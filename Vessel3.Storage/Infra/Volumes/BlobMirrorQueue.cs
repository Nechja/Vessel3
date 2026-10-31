using System.Collections.Concurrent;

namespace Vessel3.Storage;

internal sealed class BlobMirrorQueue : IBlobMirrorQueue
{
    private readonly ConcurrentQueue<string> queue = new();
    private readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> inFlight = new(StringComparer.Ordinal);

    public int PendingCount => pending.Count + inFlight.Count;

    public void Enqueue(string sha)
    {
        if (string.IsNullOrEmpty(sha) || !Sha256Hex.IsValid(sha))
            return;

        if (pending.TryAdd(sha, 1))
            queue.Enqueue(sha);
    }

    public void EnqueueBatch(IEnumerable<string> shas)
    {
        foreach (var sha in shas)
            Enqueue(sha);
    }

    public bool TryDequeue(out string? sha)
    {
        while (queue.TryDequeue(out var candidate))
        {
            inFlight.TryAdd(candidate, 1);
            pending.TryRemove(candidate, out _);
            sha = candidate;
            return true;
        }

        sha = null;
        return false;
    }

    public void Complete(string sha)
    {
        if (!string.IsNullOrEmpty(sha))
            inFlight.TryRemove(sha, out _);
    }

    public IEnumerable<string> EnumeratePendingShas()
    {
        foreach (var sha in pending.Keys)
            yield return sha;

        foreach (var sha in inFlight.Keys)
            yield return sha;
    }
}
