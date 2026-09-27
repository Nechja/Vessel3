namespace Vessel3.Server.S3;

internal sealed record CompletedPart(int Number, string Etag, CompletedPartChecksums? Sums = null);
