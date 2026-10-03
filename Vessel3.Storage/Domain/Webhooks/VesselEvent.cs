namespace Vessel3.Storage;

public sealed record VesselEvent(
    string Id,
    string Type,
    string Resource,
    DateTimeOffset Timestamp,
    string? Actor = null,
    IReadOnlyDictionary<string, string>? Properties = null,
    string? Host = null);
