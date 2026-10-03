using System.Globalization;
using System.Xml;

namespace Vessel3.Protocols.WebDav.Serialization;

internal sealed class WebDavXmlWriter : IWebDavXmlWriter
{
    public async Task WriteMultistatus(Stream output, IEnumerable<WebDavResourceEntry> entries, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, WebDavXmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "multistatus", WebDavXmlDefaults.DavNamespace);

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
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "prop", WebDavXmlDefaults.DavNamespace);
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "lockdiscovery", WebDavXmlDefaults.DavNamespace);
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "activelock", WebDavXmlDefaults.DavNamespace);

        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "locktype", WebDavXmlDefaults.DavNamespace);
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "write", WebDavXmlDefaults.DavNamespace, string.Empty);
        await w.WriteEndElementAsync();

        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "lockscope", WebDavXmlDefaults.DavNamespace);
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "exclusive", WebDavXmlDefaults.DavNamespace, string.Empty);
        await w.WriteEndElementAsync();

        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "depth", WebDavXmlDefaults.DavNamespace, "0");
        if (!string.IsNullOrEmpty(owner))
        {
            await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "owner", WebDavXmlDefaults.DavNamespace, owner);
        }
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "timeout", WebDavXmlDefaults.DavNamespace, "Second-3600");

        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "locktoken", WebDavXmlDefaults.DavNamespace);
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "href", WebDavXmlDefaults.DavNamespace, lockToken);
        await w.WriteEndElementAsync();

        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "lockroot", WebDavXmlDefaults.DavNamespace);
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "href", WebDavXmlDefaults.DavNamespace, lockRoot);
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
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "multistatus", WebDavXmlDefaults.DavNamespace);
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "response", WebDavXmlDefaults.DavNamespace);
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "href", WebDavXmlDefaults.DavNamespace, href);
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "propstat", WebDavXmlDefaults.DavNamespace);
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "prop", WebDavXmlDefaults.DavNamespace);
        await w.WriteEndElementAsync();
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "status", WebDavXmlDefaults.DavNamespace, "HTTP/1.1 200 OK");
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    private static async Task WriteResponseElement(XmlWriter w, WebDavResourceEntry entry)
    {
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "response", WebDavXmlDefaults.DavNamespace);
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "href", WebDavXmlDefaults.DavNamespace, entry.Href);
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "propstat", WebDavXmlDefaults.DavNamespace);
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "prop", WebDavXmlDefaults.DavNamespace);

        if (!string.IsNullOrEmpty(entry.DisplayName))
        {
            await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "displayname", WebDavXmlDefaults.DavNamespace, entry.DisplayName);
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
            await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "getlastmodified", WebDavXmlDefaults.DavNamespace, WebDavXmlDefaults.ToRfc1123(entry.LastModified.Value));
        }

        if (entry.CreationDate.HasValue)
        {
            await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "creationdate", WebDavXmlDefaults.DavNamespace, WebDavXmlDefaults.ToIso8601(entry.CreationDate.Value));
        }

        await w.WriteEndElementAsync();
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "status", WebDavXmlDefaults.DavNamespace, "HTTP/1.1 200 OK");
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
    }

    private static async Task WriteCollectionProperties(XmlWriter w)
    {
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "resourcetype", WebDavXmlDefaults.DavNamespace);
        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "collection", WebDavXmlDefaults.DavNamespace, string.Empty);
        await w.WriteEndElementAsync();
    }

    private static async Task WriteFileProperties(XmlWriter w, WebDavResourceEntry entry)
    {
        await w.WriteStartElementAsync(WebDavXmlDefaults.DavPrefix, "resourcetype", WebDavXmlDefaults.DavNamespace);
        await w.WriteEndElementAsync();

        await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "getcontentlength", WebDavXmlDefaults.DavNamespace, entry.ContentLength.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrEmpty(entry.ContentType))
        {
            await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "getcontenttype", WebDavXmlDefaults.DavNamespace, entry.ContentType);
        }

        if (!string.IsNullOrEmpty(entry.ETag))
        {
            var etag = entry.ETag.StartsWith('"') ? entry.ETag : $"\"{entry.ETag}\"";
            await w.WriteElementStringAsync(WebDavXmlDefaults.DavPrefix, "getetag", WebDavXmlDefaults.DavNamespace, etag);
        }
    }
}
