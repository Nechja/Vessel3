namespace Vessel3.Storage;

internal sealed record UploadMeta(
    string Bucket,
    string Key,
    string ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt);
