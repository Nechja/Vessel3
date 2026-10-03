namespace Vessel3.Storage;

public sealed record Webhook(
    string Id,
    string Name,
    string Url,
    string? Secret,
    IReadOnlyList<string> EventFilters,
    IReadOnlyList<string>? ResourceFilters,
    bool Active,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastTriggeredAt = null,
    int? LastStatusCode = null,
    string? LastError = null,
    bool IsStatic = false);
