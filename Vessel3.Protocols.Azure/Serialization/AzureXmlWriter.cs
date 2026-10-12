using System.Globalization;
using System.Xml;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Serialization;

internal interface IAzureXmlWriter
{
    Task WriteListContainers(
        Stream output,
        string serviceEndpoint,
        string? prefix,
        string? marker,
        int? maxResults,
        IEnumerable<BucketInfo> containers,
        CancellationToken ct);

    Task WriteListBlobs(
        Stream output,
        string serviceEndpoint,
        string containerName,
        string? prefix,
        string? marker,
        int? maxResults,
        string? delimiter,
        ListPage page,
        CancellationToken ct);

    Task WriteServiceProperties(Stream output, CancellationToken ct);
}

internal sealed class AzureXmlWriter : IAzureXmlWriter
{
    public async Task WriteListContainers(
        Stream output,
        string serviceEndpoint,
        string? prefix,
        string? marker,
        int? maxResults,
        IEnumerable<BucketInfo> containers,
        CancellationToken ct)
    {
        var count = (containers as IReadOnlyCollection<BucketInfo>)?.Count ?? 16;
        using var ms = new MemoryStream(Math.Max(512, count * 350));
        using (var w = XmlWriter.Create(ms, AzureXmlDefaults.WriterSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement(null, "EnumerationResults", null);
            w.WriteAttributeString(null, "ServiceEndpoint", null, serviceEndpoint);

            if (!string.IsNullOrEmpty(prefix))
            {
                w.WriteElementString(null, "Prefix", null, prefix);
            }

            if (!string.IsNullOrEmpty(marker))
            {
                w.WriteElementString(null, "Marker", null, marker);
            }

            if (maxResults.HasValue)
            {
                w.WriteElementString(null, "MaxResults", null, maxResults.Value.ToString(CultureInfo.InvariantCulture));
            }

            w.WriteStartElement(null, "Containers", null);
            foreach (var c in containers)
            {
                ct.ThrowIfCancellationRequested();
                w.WriteStartElement(null, "Container", null);
                w.WriteElementString(null, "Name", null, c.Name);

                w.WriteStartElement(null, "Properties", null);
                w.WriteElementString(null, "Last-Modified", null, AzureXmlDefaults.ToRfc1123(c.CreatedAt));
                w.WriteElementString(null, "Etag", null, $"\"{c.Name}\"");
                w.WriteElementString(null, "LeaseStatus", null, "unlocked");
                w.WriteElementString(null, "LeaseState", null, "available");
                w.WriteElementString(null, "DefaultEncryptionScope", null, "$account-encryption-key");
                w.WriteElementString(null, "DenyEncryptionScopeOverride", null, "false");
                w.WriteElementString(null, "HasImmutabilityPolicy", null, "false");
                w.WriteElementString(null, "HasLegalHold", null, "false");
                w.WriteEndElement();

                w.WriteEndElement();
            }
            w.WriteEndElement();

            w.WriteElementString(null, "NextMarker", null, "");

            w.WriteEndElement();
            w.WriteEndDocument();
            w.Flush();
        }

        await output.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), ct);
    }

    public async Task WriteListBlobs(
        Stream output,
        string serviceEndpoint,
        string containerName,
        string? prefix,
        string? marker,
        int? maxResults,
        string? delimiter,
        ListPage page,
        CancellationToken ct)
    {
        using var ms = new MemoryStream(Math.Max(512, page.Entries.Count * 300));
        using (var w = XmlWriter.Create(ms, AzureXmlDefaults.WriterSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement(null, "EnumerationResults", null);
            w.WriteAttributeString(null, "ServiceEndpoint", null, serviceEndpoint);
            w.WriteAttributeString(null, "ContainerName", null, containerName);

            if (!string.IsNullOrEmpty(prefix))
            {
                w.WriteElementString(null, "Prefix", null, prefix);
            }

            if (!string.IsNullOrEmpty(marker))
            {
                w.WriteElementString(null, "Marker", null, marker);
            }

            if (maxResults.HasValue)
            {
                w.WriteElementString(null, "MaxResults", null, maxResults.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrEmpty(delimiter))
            {
                w.WriteElementString(null, "Delimiter", null, delimiter);
            }

            w.WriteStartElement(null, "Blobs", null);
            foreach (var entry in page.Entries)
            {
                ct.ThrowIfCancellationRequested();
                switch (entry)
                {
                    case ListEntry.Contents c:
                        w.WriteStartElement(null, "Blob", null);
                        w.WriteElementString(null, "Name", null, c.Key);
                        w.WriteStartElement(null, "Properties", null);
                        w.WriteElementString(null, "Creation-Time", null, AzureXmlDefaults.ToRfc1123(c.LastModified));
                        w.WriteElementString(null, "Last-Modified", null, AzureXmlDefaults.ToRfc1123(c.LastModified));
                        w.WriteElementString(null, "Etag", null, c.Etag.StartsWith('"') ? c.Etag : $"\"{c.Etag}\"");
                        w.WriteElementString(null, "Content-Length", null, c.Size.ToString(CultureInfo.InvariantCulture));
                        w.WriteElementString(null, "Content-Type", null, "application/octet-stream");
                        w.WriteElementString(null, "BlobType", null, "BlockBlob");
                        w.WriteElementString(null, "AccessTier", null, "Hot");
                        w.WriteElementString(null, "AccessTierInferred", null, "true");
                        w.WriteElementString(null, "LeaseStatus", null, "unlocked");
                        w.WriteElementString(null, "LeaseState", null, "available");
                        w.WriteElementString(null, "ServerEncrypted", null, "true");
                        w.WriteEndElement();
                        w.WriteEndElement();
                        break;

                    case ListEntry.CommonPrefix p:
                        w.WriteStartElement(null, "BlobPrefix", null);
                        w.WriteElementString(null, "Name", null, p.Key);
                        w.WriteEndElement();
                        break;
                }
            }
            w.WriteEndElement();

            w.WriteElementString(null, "NextMarker", null, page.NextContinuationToken ?? "");

            w.WriteEndElement();
            w.WriteEndDocument();
            w.Flush();
        }

        await output.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), ct);
    }

    public async Task WriteServiceProperties(Stream output, CancellationToken ct)
    {
        using var ms = new MemoryStream(1024);
        using (var w = XmlWriter.Create(ms, AzureXmlDefaults.WriterSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement(null, "StorageServiceProperties", null);

            w.WriteStartElement(null, "Logging", null);
            w.WriteElementString(null, "Version", null, "1.0");
            w.WriteElementString(null, "Delete", null, "false");
            w.WriteElementString(null, "Read", null, "false");
            w.WriteElementString(null, "Write", null, "false");
            w.WriteStartElement(null, "RetentionPolicy", null);
            w.WriteElementString(null, "Enabled", null, "false");
            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteStartElement(null, "HourMetrics", null);
            w.WriteElementString(null, "Version", null, "1.0");
            w.WriteElementString(null, "Enabled", null, "false");
            w.WriteStartElement(null, "RetentionPolicy", null);
            w.WriteElementString(null, "Enabled", null, "false");
            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteStartElement(null, "MinuteMetrics", null);
            w.WriteElementString(null, "Version", null, "1.0");
            w.WriteElementString(null, "Enabled", null, "false");
            w.WriteStartElement(null, "RetentionPolicy", null);
            w.WriteElementString(null, "Enabled", null, "false");
            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteStartElement(null, "Cors", null);
            w.WriteEndElement();

            w.WriteEndElement();
            w.WriteEndDocument();
            w.Flush();
        }

        await output.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), ct);
    }
}
