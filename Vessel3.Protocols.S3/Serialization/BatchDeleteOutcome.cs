namespace Vessel3.Server.S3;

internal sealed record BatchDeleteOutcome(string Key, string? VersionId, Error? Error);
