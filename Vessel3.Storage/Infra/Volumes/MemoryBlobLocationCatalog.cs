using System.Collections.Concurrent;

namespace Vessel3.Storage;

internal sealed class MemoryBlobLocationCatalog : IBlobLocationCatalog
{
    private readonly ConcurrentDictionary<string, string> locations = new(StringComparer.Ordinal);

    public string? LocateBlob(string sha) =>
        locations.TryGetValue(sha, out var v) ? v : null;

    public void RecordLocation(string sha, string volumeId) =>
        locations[sha] = volumeId;

    public void RemoveLocation(string sha) =>
        locations.TryRemove(sha, out _);
}
