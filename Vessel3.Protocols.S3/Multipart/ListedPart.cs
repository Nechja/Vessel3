namespace Vessel3.Server.S3;

internal sealed record ListedPart(int Number, string Etag, long Size, DateTimeOffset LastModified);
