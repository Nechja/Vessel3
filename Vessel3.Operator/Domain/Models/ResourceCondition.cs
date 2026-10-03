namespace Vessel3.Operator.Domain.Models;

public sealed record ResourceCondition(
    string Type,
    string Status,
    string Reason,
    string Message);
