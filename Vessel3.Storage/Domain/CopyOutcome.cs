namespace Vessel3.Storage;

internal sealed record CopyOutcome(string Etag, DateTimeOffset LastModified, string VersionId);
