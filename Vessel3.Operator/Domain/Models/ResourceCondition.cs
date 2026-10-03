namespace Vessel3.Operator.Domain.Models;

public sealed record ResourceCondition(
    string Type,
    string Status,
    string Reason,
    string Message)
{
    public static ResourceCondition Ready(bool isReady, string? errorMessage = null) =>
        new(
            ConditionTypes.Ready,
            isReady ? ConditionTypes.StatusTrue : ConditionTypes.StatusFalse,
            isReady ? ConditionTypes.ReasonReconciled : ConditionTypes.ReasonReconcileFailed,
            errorMessage ?? string.Empty);
}
