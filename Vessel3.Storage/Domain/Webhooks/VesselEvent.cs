namespace Vessel3.Storage;

public sealed record VesselEvent(
    string Id,
    string Type,
    string Source,
    string Subject,
    DateTimeOffset Time,
    IReadOnlyDictionary<string, string>? Data = null,
    string? DataContentType = "application/json",
    string SpecVersion = "1.0",
    string? Actor = null,
    string? Host = null);
