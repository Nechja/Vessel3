namespace Vessel3.Operator.Domain;

public readonly record struct ReconciliationOutcome(bool IsSuccessful, TimeSpan? RequeueAfter = null, string? ErrorMessage = null)
{
    public static ReconciliationOutcome Success() => new(true);
    public static ReconciliationOutcome Requeue(TimeSpan delay) => new(true, delay);
    public static ReconciliationOutcome Failure(string error) => new(false, ErrorMessage: error);
}
