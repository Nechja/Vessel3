namespace Vessel3.Storage;

public sealed class ServerLogBuffer(int capacity = 1_000) : IServerLogBuffer
{
    private readonly ServerLogEntry?[] entries = new ServerLogEntry?[Math.Max(1, capacity)];
    private readonly Lock sync = new();
    private int head;
    private int count;

    public void Log(ServerLogEntry entry)
    {
        lock (sync)
        {
            entries[head] = entry;
            head = (head + 1) % entries.Length;
            if (count < entries.Length)
            {
                count++;
            }
        }
    }

    public IReadOnlyList<ServerLogEntry> GetRecent(int limit = 100, string? level = null, string? protocol = null)
    {
        var max = Math.Clamp(limit, 1, 1_000);
        var result = new List<ServerLogEntry>(Math.Min(max, count));

        lock (sync)
        {
            for (var i = 0; i < count && result.Count < max; i++)
            {
                var idx = (head - 1 - i + entries.Length) % entries.Length;
                var item = entries[idx];
                if (item is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(level) && !string.Equals(item.Level, level, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(protocol) && !string.Equals(item.Protocol, protocol, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(item);
            }
        }

        return result;
    }

    public void Clear()
    {
        lock (sync)
        {
            Array.Clear(entries);
            head = 0;
            count = 0;
        }
    }
}
