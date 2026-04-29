using System.Collections.Concurrent;

namespace Vessel3.Server;

internal sealed class ListCursorStore
{
    private readonly ConcurrentDictionary<string, ListCursor> cursors = new();
    private readonly TimeSpan ttl = TimeSpan.FromMinutes(5);

    public string Save(ListCursor cursor)
    {
        Sweep();
        var token = Guid.NewGuid().ToString("N");
        cursors[token] = cursor;
        return token;
    }

    public Result<ListCursor> Resume(string token) =>
        cursors.TryGetValue(token, out var cursor)
            ? cursor
            : new NotFoundError($"cursor {token}");

    public void Drop(string token) => cursors.TryRemove(token, out _);

    private void Sweep()
    {
        var cutoff = DateTimeOffset.UtcNow - ttl;
        foreach (var (k, v) in cursors)
        {
            if (v.CreatedAt < cutoff) cursors.TryRemove(k, out _);
        }
    }
}
