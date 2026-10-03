namespace Vessel3.Protocols.WebDav.Serialization;

public sealed record WebDavResourceEntry(
    string Href,
    string DisplayName,
    bool IsCollection,
    long ContentLength,
    string? ContentType,
    string? ETag,
    DateTimeOffset? LastModified,
    DateTimeOffset? CreationDate);

internal interface IWebDavXmlWriter
{
    Task WriteMultistatus(Stream output, IEnumerable<WebDavResourceEntry> entries, CancellationToken ct);
    Task WriteLockDiscovery(Stream output, string lockRoot, string lockToken, string? owner, CancellationToken ct);
    Task WriteProppatchResponse(Stream output, string href, CancellationToken ct);
}
