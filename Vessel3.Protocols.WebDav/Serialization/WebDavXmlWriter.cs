using System.Globalization;
using System.Xml;
using Vessel3.IO;

namespace Vessel3.Protocols.WebDav.Serialization;

internal sealed class WebDavXmlWriter : IWebDavXmlWriter
{
    public async Task WriteMultistatus(Stream output, IEnumerable<WebDavResourceEntry> entries, CancellationToken ct)
    {
        using var buffer = new PooledBufferStream();
        using (var w = XmlWriter.Create(buffer, WebDavXmlDefaults.WriterSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Multistatus, WebDavXmlDefaults.DavNamespace);

            foreach (var entry in entries)
            {
                WriteResponseElement(w, entry);
            }

            w.WriteEndElement();
            w.WriteEndDocument();
            w.Flush();
        }

        await buffer.WriteTo(output, ct);
    }

    public async Task WriteLockDiscovery(Stream output, string lockRoot, string lockToken, string? owner, CancellationToken ct)
    {
        using var buffer = new PooledBufferStream();
        using (var w = XmlWriter.Create(buffer, WebDavXmlDefaults.WriterSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Prop, WebDavXmlDefaults.DavNamespace);
            w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.LockDiscovery, WebDavXmlDefaults.DavNamespace);
            w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.ActiveLock, WebDavXmlDefaults.DavNamespace);

            w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.LockType, WebDavXmlDefaults.DavNamespace);
            w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Write, WebDavXmlDefaults.DavNamespace, string.Empty);
            w.WriteEndElement();

            w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.LockScope, WebDavXmlDefaults.DavNamespace);
            w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Exclusive, WebDavXmlDefaults.DavNamespace, string.Empty);
            w.WriteEndElement();

            w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Depth, WebDavXmlDefaults.DavNamespace, "0");
            if (!string.IsNullOrEmpty(owner))
            {
                w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Owner, WebDavXmlDefaults.DavNamespace, owner);
            }
            w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Timeout, WebDavXmlDefaults.DavNamespace, "Second-3600");

            w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.LockToken, WebDavXmlDefaults.DavNamespace);
            w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Href, WebDavXmlDefaults.DavNamespace, lockToken);
            w.WriteEndElement();

            w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.LockRoot, WebDavXmlDefaults.DavNamespace);
            w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Href, WebDavXmlDefaults.DavNamespace, lockRoot);
            w.WriteEndElement();

            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
            w.Flush();
        }

        await buffer.WriteTo(output, ct);
    }

    public async Task WriteProppatchResponse(Stream output, string href, CancellationToken ct)
    {
        using var buffer = new PooledBufferStream();
        using (var w = XmlWriter.Create(buffer, WebDavXmlDefaults.WriterSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Multistatus, WebDavXmlDefaults.DavNamespace);
            w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Response, WebDavXmlDefaults.DavNamespace);
            w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Href, WebDavXmlDefaults.DavNamespace, href);
            w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Propstat, WebDavXmlDefaults.DavNamespace);
            w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Prop, WebDavXmlDefaults.DavNamespace);
            w.WriteEndElement();
            w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Status, WebDavXmlDefaults.DavNamespace, WebDavHttpStatusStrings.Http200);
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
            w.Flush();
        }

        await buffer.WriteTo(output, ct);
    }

    private static void WriteResponseElement(XmlWriter w, WebDavResourceEntry entry)
    {
        w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Response, WebDavXmlDefaults.DavNamespace);
        w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Href, WebDavXmlDefaults.DavNamespace, entry.Href);
        w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Propstat, WebDavXmlDefaults.DavNamespace);
        w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Prop, WebDavXmlDefaults.DavNamespace);

        if (!string.IsNullOrEmpty(entry.DisplayName))
        {
            w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.DisplayName, WebDavXmlDefaults.DavNamespace, entry.DisplayName);
        }

        if (entry.IsCollection)
        {
            WriteCollectionProperties(w);
        }
        else
        {
            WriteFileProperties(w, entry);
        }

        if (entry.LastModified.HasValue)
        {
            w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.GetLastModified, WebDavXmlDefaults.DavNamespace, WebDavXmlDefaults.ToRfc1123(entry.LastModified.Value));
        }

        if (entry.CreationDate.HasValue)
        {
            w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.CreationDate, WebDavXmlDefaults.DavNamespace, WebDavXmlDefaults.ToIso8601(entry.CreationDate.Value));
        }

        w.WriteEndElement();
        w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Status, WebDavXmlDefaults.DavNamespace, WebDavHttpStatusStrings.Http200);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void WriteCollectionProperties(XmlWriter w)
    {
        w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.ResourceType, WebDavXmlDefaults.DavNamespace);
        w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.Collection, WebDavXmlDefaults.DavNamespace, string.Empty);
        w.WriteEndElement();
    }

    private static void WriteFileProperties(XmlWriter w, WebDavResourceEntry entry)
    {
        w.WriteStartElement(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.ResourceType, WebDavXmlDefaults.DavNamespace);
        w.WriteEndElement();

        w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.GetContentLength, WebDavXmlDefaults.DavNamespace, entry.ContentLength.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrEmpty(entry.ContentType))
        {
            w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.GetContentType, WebDavXmlDefaults.DavNamespace, entry.ContentType);
        }

        if (!string.IsNullOrEmpty(entry.ETag))
        {
            var etag = entry.ETag.StartsWith('"') ? entry.ETag : $"\"{entry.ETag}\"";
            w.WriteElementString(WebDavXmlDefaults.DavPrefix, WebDavXmlElements.GetETag, WebDavXmlDefaults.DavNamespace, etag);
        }
    }
}
