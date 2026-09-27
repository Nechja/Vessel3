namespace Vessel3.Storage;

internal readonly record struct PreconditionRules(
    string? IfMatch = null,
    string? IfNoneMatch = null,
    string? IfModifiedSince = null,
    string? IfUnmodifiedSince = null);

internal readonly record struct WritePreconditions(
    string? IfMatch = null,
    string? IfNoneMatch = null);
