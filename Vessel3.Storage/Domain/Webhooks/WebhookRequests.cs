namespace Vessel3.Storage;

public sealed record CreateWebhookRequest(
    string Name,
    string Url,
    string? Secret,
    IReadOnlyList<string> EventFilters,
    IReadOnlyList<string>? ResourceFilters = null,
    bool Active = true);

public sealed record UpdateWebhookRequest(
    string Name,
    string Url,
    string? Secret,
    IReadOnlyList<string> EventFilters,
    IReadOnlyList<string>? ResourceFilters = null,
    bool Active = true);

public sealed record WebhookDeliveryResult(
    string WebhookId,
    bool Success,
    int? StatusCode,
    TimeSpan Latency,
    string? ErrorMessage = null,
    string? ResponseBody = null);
