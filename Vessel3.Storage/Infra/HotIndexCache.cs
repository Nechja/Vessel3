using System.Collections.Concurrent;

namespace Vessel3.Storage;

internal sealed class HotIndexCache(int maxCapacity = 50_000)
{
    private readonly ConcurrentDictionary<string, CacheEntry> entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> evictionQueue = new();

    private readonly record struct CacheEntry(PutEntry? Put, bool Exists);

    public bool TryGet(string key, out PutEntry? entry)
    {
        if (entries.TryGetValue(key, out var cached))
        {
            entry = cached.Put;
            return true;
        }

        entry = null;
        return false;
    }

    public void Set(string key, PutEntry? entry)
    {
        var isNew = !entries.ContainsKey(key);
        entries[key] = new CacheEntry(entry, entry is not null);

        if (isNew)
        {
            evictionQueue.Enqueue(key);
            TrimExcess();
        }
    }

    public void Evict(string key) => entries.TryRemove(key, out _);

    public void UpdateTags(string key, string versionId, IReadOnlyDictionary<string, string> tags)
    {
        if (!entries.TryGetValue(key, out var cached) || cached.Put is not { } put || put.VersionId != versionId)
            return;

        entries[key] = cached with { Put = put with { Tags = tags } };
    }

    public void UpdateRetention(string key, string versionId, Retention? retention)
    {
        if (!entries.TryGetValue(key, out var cached) || cached.Put is not { } put || put.VersionId != versionId)
            return;

        entries[key] = cached with { Put = put with { Retention = retention } };
    }

    public void UpdateLegalHold(string key, string versionId, bool legalHold)
    {
        if (!entries.TryGetValue(key, out var cached) || cached.Put is not { } put || put.VersionId != versionId)
            return;

        entries[key] = cached with { Put = put with { LegalHoldOn = legalHold } };
    }

    public void Clear()
    {
        entries.Clear();
        while (evictionQueue.TryDequeue(out _)) { }
    }

    private void TrimExcess()
    {
        while (entries.Count > maxCapacity && evictionQueue.TryDequeue(out var oldest))
            entries.TryRemove(oldest, out _);
    }
}
