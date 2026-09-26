namespace Vessel3.Storage;

internal sealed record CompletedPart(int Number, string Etag, CompletedPartChecksums? Sums = null);
