using System.Collections.Concurrent;

namespace Vessel3.Storage;

internal interface IBlobMirrorQueue
{
    void Enqueue(string sha);
    void EnqueueBatch(IEnumerable<string> shas);
    bool TryDequeue(out string? sha);
    IEnumerable<string> EnumeratePendingShas();
}

internal sealed class BlobMirrorQueue : IBlobMirrorQueue
{
    private readonly ConcurrentQueue<string> queue = new();
    private readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.Ordinal);

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
            pending.TryRemove(candidate, out _);
            sha = candidate;
            return true;
        }

        sha = null;
        return false;
    }

    public IEnumerable<string> EnumeratePendingShas() => pending.Keys;
}
