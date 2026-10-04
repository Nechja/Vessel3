namespace Vessel3.Storage;

public sealed record ServerLogEntry(
    string Id,
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message,
    string? Protocol = null,
    string? Action = null,
    string? Subject = null,
    string? Actor = null,
    int? StatusCode = null,
    double? DurationMs = null,
    string? TraceId = null,
    string? ErrorDetails = null);

public interface IServerLogBuffer
{
    void Log(ServerLogEntry entry);
    IReadOnlyList<ServerLogEntry> GetRecent(int limit = 100, string? level = null, string? protocol = null);
    void Clear();
}
