using System.Collections.Concurrent;

namespace Vessel3.Storage;

internal sealed class HotIndexCache(int maxCapacity = 50_000)
{
    private readonly Lock gate = new();
    private readonly ConcurrentDictionary<string, CacheNode> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> lru = new();
    private long versionSequence;

    private sealed class CacheEntry(PutEntry? put, bool isNegativeCache, long version)
    {
        public PutEntry? Put { get; } = put;
        public bool IsNegativeCache { get; } = isNegativeCache;
        public long Version { get; } = version;
    }

    private sealed class CacheNode
    {
        public volatile CacheEntry? Current;
        public LinkedListNode<string>? ListNode;
    }

    public bool TryGet(string key, out PutEntry? entry)
    {
        if (entries.TryGetValue(key, out var node) && node.Current is { } current)
        {
            if (current.Put is not null)
            {
                entry = current.Put;
                return true;
            }
            if (current.IsNegativeCache)
            {
                entry = null;
                return true;
            }
        }

        entry = null;
        return false;
    }

    public long GetVersion(string key) =>
        entries.TryGetValue(key, out var node) && node.Current is { } curr ? curr.Version : 0;

    public void Set(string key, PutEntry? entry)
    {
        lock (gate)
        {
            var newEntry = new CacheEntry(entry, isNegativeCache: entry is null, version: Interlocked.Increment(ref versionSequence));
            if (entries.TryGetValue(key, out var node))
            {
                node.Current = newEntry;
                if (node.ListNode is not null)
                {
                    lru.Remove(node.ListNode);
                    lru.AddLast(node.ListNode);
                }
                else
                {
                    node.ListNode = lru.AddLast(key);
                }
                return;
            }

            var newNode = new CacheNode
            {
                Current = newEntry,
                ListNode = lru.AddLast(key)
            };
            entries[key] = newNode;
            TrimExcess();
        }
    }

    public bool TryFill(string key, PutEntry? entry, long expectedVersion)
    {
        lock (gate)
        {
            var currentVersion = entries.TryGetValue(key, out var node) && node.Current is { } curr ? curr.Version : 0;
            if (currentVersion != expectedVersion)
            {
                return false;
            }

            var newEntry = new CacheEntry(entry, isNegativeCache: entry is null, version: Interlocked.Increment(ref versionSequence));
            if (node is not null)
            {
                node.Current = newEntry;
                if (node.ListNode is not null)
                {
                    lru.Remove(node.ListNode);
                    lru.AddLast(node.ListNode);
                }
                else
                {
                    node.ListNode = lru.AddLast(key);
                }
                return true;
            }

            var newNode = new CacheNode
            {
                Current = newEntry,
                ListNode = lru.AddLast(key)
            };
            entries[key] = newNode;
            TrimExcess();
            return true;
        }
    }

    public void Evict(string key)
    {
        lock (gate)
        {
            var newEntry = new CacheEntry(put: null, isNegativeCache: false, version: Interlocked.Increment(ref versionSequence));
            if (entries.TryGetValue(key, out var node))
            {
                node.Current = newEntry;
                if (node.ListNode is not null)
                {
                    lru.Remove(node.ListNode);
                    lru.AddLast(node.ListNode);
                }
                else
                {
                    node.ListNode = lru.AddLast(key);
                }
                return;
            }

            var newNode = new CacheNode
            {
                Current = newEntry,
                ListNode = lru.AddLast(key)
            };
            entries[key] = newNode;
            TrimExcess();
        }
    }

    public void UpdateTags(string key, string versionId, IReadOnlyDictionary<string, string> tags)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var node) || node.Current is not { Put: { } put } curr || put.VersionId != versionId)
                return;

            node.Current = new CacheEntry(put with { Tags = tags }, isNegativeCache: false, version: Interlocked.Increment(ref versionSequence));
        }
    }

    public void UpdateRetention(string key, string versionId, Retention? retention)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var node) || node.Current is not { Put: { } put } curr || put.VersionId != versionId)
                return;

            node.Current = new CacheEntry(put with { Retention = retention }, isNegativeCache: false, version: Interlocked.Increment(ref versionSequence));
        }
    }

    public void UpdateLegalHold(string key, string versionId, bool legalHold)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var node) || node.Current is not { Put: { } put } curr || put.VersionId != versionId)
                return;

            node.Current = new CacheEntry(put with { LegalHoldOn = legalHold }, isNegativeCache: false, version: Interlocked.Increment(ref versionSequence));
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            lru.Clear();
        }
    }

    private void TrimExcess()
    {
        while (entries.Count > maxCapacity && lru.First is { } first)
        {
            lru.RemoveFirst();
            entries.TryRemove(first.Value, out _);
        }
    }
}
