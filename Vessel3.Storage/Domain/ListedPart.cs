namespace Vessel3.Storage;

internal sealed record ListedPart(int Number, string Etag, long Size, DateTimeOffset LastModified);
