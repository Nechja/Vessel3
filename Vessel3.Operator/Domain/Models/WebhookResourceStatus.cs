namespace Vessel3.Operator.Domain.Models;

public sealed record WebhookResourceStatus(
    string Phase,
    string? WebhookId = null,
    DateTimeOffset? LastTriggeredAt = null,
    int? LastStatusCode = null,
    string? ErrorMessage = null,
    IReadOnlyList<ResourceCondition>? Conditions = null);
