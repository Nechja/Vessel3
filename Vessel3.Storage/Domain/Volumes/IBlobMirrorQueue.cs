namespace Vessel3.Storage;

internal interface IBlobMirrorQueue
{
    int PendingCount { get; }
    void Enqueue(string sha);
    void EnqueueBatch(IEnumerable<string> shas);
    bool TryDequeue(out string? sha);
    void Complete(string sha);
    IEnumerable<string> EnumeratePendingShas();
}
