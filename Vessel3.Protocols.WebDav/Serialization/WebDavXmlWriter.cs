using System.Globalization;
using System.Xml;

namespace Vessel3.Protocols.WebDav.Serialization;

internal sealed class WebDavXmlWriter : IWebDavXmlWriter
{
    public async Task WriteMultistatus(Stream output, IEnumerable<WebDavResourceEntry> entries, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, WebDavXmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Multistatus, WebDavXmlDefaults.DavNamespace);

        foreach (var entry in entries)
        {
            await WriteResponseElement(w, entry);
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteLockDiscovery(Stream output, string lockRoot, string lockToken, string? owner, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, WebDavXmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Prop, WebDavXmlDefaults.DavNamespace);
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.LockDiscovery, WebDavXmlDefaults.DavNamespace);
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.ActiveLock, WebDavXmlDefaults.DavNamespace);

        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.LockType, WebDavXmlDefaults.DavNamespace);
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Write, WebDavXmlDefaults.DavNamespace, string.Empty);
        await w.WriteEndElementAsync();

        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.LockScope, WebDavXmlDefaults.DavNamespace);
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Exclusive, WebDavXmlDefaults.DavNamespace, string.Empty);
        await w.WriteEndElementAsync();

        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Depth, WebDavXmlDefaults.DavNamespace, "0");
        if (!string.IsNullOrEmpty(owner))
        {
            await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Owner, WebDavXmlDefaults.DavNamespace, owner);
        }
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Timeout, WebDavXmlDefaults.DavNamespace, "Second-3600");

        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.LockToken, WebDavXmlDefaults.DavNamespace);
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Href, WebDavXmlDefaults.DavNamespace, lockToken);
        await w.WriteEndElementAsync();

        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.LockRoot, WebDavXmlDefaults.DavNamespace);
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Href, WebDavXmlDefaults.DavNamespace, lockRoot);
        await w.WriteEndElementAsync();

        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteProppatchResponse(Stream output, string href, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, WebDavXmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Multistatus, WebDavXmlDefaults.DavNamespace);
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Response, WebDavXmlDefaults.DavNamespace);
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Href, WebDavXmlDefaults.DavNamespace, href);
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Propstat, WebDavXmlDefaults.DavNamespace);
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Prop, WebDavXmlDefaults.DavNamespace);
        await w.WriteEndElementAsync();
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Status, WebDavXmlDefaults.DavNamespace, WebDavHttpStatusStrings.Http200);
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    private static async Task WriteResponseElement(XmlWriter w, WebDavResourceEntry entry)
    {
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Response, WebDavXmlDefaults.DavNamespace);
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Href, WebDavXmlDefaults.DavNamespace, entry.Href);
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Propstat, WebDavXmlDefaults.DavNamespace);
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Prop, WebDavXmlDefaults.DavNamespace);

        if (!string.IsNullOrEmpty(entry.DisplayName))
        {
            await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.DisplayName, WebDavXmlDefaults.DavNamespace, entry.DisplayName);
        }

        if (entry.IsCollection)
        {
            await WriteCollectionProperties(w);
        }
        else
        {
            await WriteFileProperties(w, entry);
        }

        if (entry.LastModified.HasValue)
        {
            await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.GetLastModified, WebDavXmlDefaults.DavNamespace, WebDavXmlDefaults.ToRfc1123(entry.LastModified.Value));
        }

        if (entry.CreationDate.HasValue)
        {
            await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.CreationDate, WebDavXmlDefaults.DavNamespace, WebDavXmlDefaults.ToIso8601(entry.CreationDate.Value));
        }

        await w.WriteEndElementAsync();
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Status, WebDavXmlDefaults.DavNamespace, WebDavHttpStatusStrings.Http200);
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
    }

    private static async Task WriteCollectionProperties(XmlWriter w)
    {
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.ResourceType, WebDavXmlDefaults.DavNamespace);
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Collection, WebDavXmlDefaults.DavNamespace, string.Empty);
        await w.WriteEndElementAsync();
    }

    private static async Task WriteFileProperties(XmlWriter w, WebDavResourceEntry entry)
    {
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.ResourceType, WebDavXmlDefaults.DavNamespace);
        await w.WriteEndElementAsync();

        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.GetContentLength, WebDavXmlDefaults.DavNamespace, entry.ContentLength.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrEmpty(entry.ContentType))
        {
            await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.GetContentType, WebDavXmlDefaults.DavNamespace, entry.ContentType);
        }

        if (!string.IsNullOrEmpty(entry.ETag))
        {
            var etag = entry.ETag.StartsWith('"') ? entry.ETag : $"\"{entry.ETag}\"";
            await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.GetETag, WebDavXmlDefaults.DavNamespace, etag);
        }
    }
}
