namespace Vessel3.Server.S3;

internal sealed record BatchDeleteKey(string Key, string? VersionId);
