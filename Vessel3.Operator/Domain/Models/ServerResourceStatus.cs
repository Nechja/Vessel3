namespace Vessel3.Operator.Domain.Models;

public sealed record ServerResourceStatus(
    string Phase,
    string? Endpoint = null,
    string? AdminSecret = null,
    int ReadyReplicas = 0,
    IReadOnlyList<ResourceCondition>? Conditions = null);
