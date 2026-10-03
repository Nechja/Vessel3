namespace Vessel3.Operator.Domain.Models;

public sealed record BucketResourceStatus(
    string Phase,
    long SizeBytes = 0,
    long ObjectCount = 0,
    string? ErrorMessage = null,
    IReadOnlyList<ResourceCondition>? Conditions = null);
