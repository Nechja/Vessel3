namespace Vessel3.Operator.Domain.Models;

public sealed record UserStatus(
    string Phase,
    string? UserId = null,
    string? SecretRef = null,
    string? ErrorMessage = null,
    IReadOnlyList<ResourceCondition>? Conditions = null);
