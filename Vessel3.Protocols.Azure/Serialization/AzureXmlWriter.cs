using System.Security;
using System.Xml;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Serialization;

internal interface IAzureXmlWriter
{
    Task WriteListContainersAsync(
        Stream output,
        string serviceEndpoint,
        string? prefix,
        string? marker,
        int? maxResults,
        IEnumerable<BucketInfo> containers,
        CancellationToken ct);

    Task WriteListBlobsAsync(
        Stream output,
        string serviceEndpoint,
        string containerName,
        string? prefix,
        string? marker,
        int? maxResults,
        string? delimiter,
        ListPage page,
        CancellationToken ct);

    Task WriteServicePropertiesAsync(Stream output, CancellationToken ct);
}

internal sealed class AzureXmlWriter : IAzureXmlWriter
{
    public async Task WriteListContainersAsync(
        Stream output,
        string serviceEndpoint,
        string? prefix,
        string? marker,
        int? maxResults,
        IEnumerable<BucketInfo> containers,
        CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, AzureXmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "EnumerationResults", null);
        await w.WriteAttributeStringAsync(null, "ServiceEndpoint", null, serviceEndpoint);

        if (!string.IsNullOrEmpty(prefix))
        {
            await w.WriteElementStringAsync(null, "Prefix", null, prefix);
        }

        if (!string.IsNullOrEmpty(marker))
        {
            await w.WriteElementStringAsync(null, "Marker", null, marker);
        }

        if (maxResults.HasValue)
        {
            await w.WriteElementStringAsync(null, "MaxResults", null, maxResults.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        await w.WriteStartElementAsync(null, "Containers", null);
        foreach (var c in containers)
        {
            ct.ThrowIfCancellationRequested();
            await w.WriteStartElementAsync(null, "Container", null);
            await w.WriteElementStringAsync(null, "Name", null, c.Name);

            await w.WriteStartElementAsync(null, "Properties", null);
            await w.WriteElementStringAsync(null, "Last-Modified", null, AzureXmlDefaults.ToRfc1123(c.CreatedAt));
            await w.WriteElementStringAsync(null, "Etag", null, $"\"{c.Name}\"");
            await w.WriteElementStringAsync(null, "LeaseStatus", null, "unlocked");
            await w.WriteElementStringAsync(null, "LeaseState", null, "available");
            await w.WriteElementStringAsync(null, "DefaultEncryptionScope", null, "$account-encryption-key");
            await w.WriteElementStringAsync(null, "DenyEncryptionScopeOverride", null, "false");
            await w.WriteElementStringAsync(null, "HasImmutabilityPolicy", null, "false");
            await w.WriteElementStringAsync(null, "HasLegalHold", null, "false");
            await w.WriteEndElementAsync(); // Properties

            await w.WriteEndElementAsync(); // Container
        }
        await w.WriteEndElementAsync(); // Containers

        await w.WriteElementStringAsync(null, "NextMarker", null, "");

        await w.WriteEndElementAsync(); // EnumerationResults
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteListBlobsAsync(
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
        await using var w = XmlWriter.Create(output, AzureXmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "EnumerationResults", null);
        await w.WriteAttributeStringAsync(null, "ServiceEndpoint", null, serviceEndpoint);
        await w.WriteAttributeStringAsync(null, "ContainerName", null, containerName);

        if (!string.IsNullOrEmpty(prefix))
        {
            await w.WriteElementStringAsync(null, "Prefix", null, prefix);
        }

        if (!string.IsNullOrEmpty(marker))
        {
            await w.WriteElementStringAsync(null, "Marker", null, marker);
        }

        if (maxResults.HasValue)
        {
            await w.WriteElementStringAsync(null, "MaxResults", null, maxResults.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(delimiter))
        {
            await w.WriteElementStringAsync(null, "Delimiter", null, delimiter);
        }

        await w.WriteStartElementAsync(null, "Blobs", null);
        foreach (var entry in page.Entries)
        {
            ct.ThrowIfCancellationRequested();
            switch (entry)
            {
                case ListEntry.Contents c:
                    await w.WriteStartElementAsync(null, "Blob", null);
                    await w.WriteElementStringAsync(null, "Name", null, c.Key);
                    await w.WriteStartElementAsync(null, "Properties", null);
                    await w.WriteElementStringAsync(null, "Creation-Time", null, AzureXmlDefaults.ToRfc1123(c.LastModified));
                    await w.WriteElementStringAsync(null, "Last-Modified", null, AzureXmlDefaults.ToRfc1123(c.LastModified));
                    await w.WriteElementStringAsync(null, "Etag", null, c.Etag.StartsWith('"') ? c.Etag : $"\"{c.Etag}\"");
                    await w.WriteElementStringAsync(null, "Content-Length", null, c.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await w.WriteElementStringAsync(null, "Content-Type", null, "application/octet-stream");
                    await w.WriteElementStringAsync(null, "BlobType", null, "BlockBlob");
                    await w.WriteElementStringAsync(null, "AccessTier", null, "Hot");
                    await w.WriteElementStringAsync(null, "AccessTierInferred", null, "true");
                    await w.WriteElementStringAsync(null, "LeaseStatus", null, "unlocked");
                    await w.WriteElementStringAsync(null, "LeaseState", null, "available");
                    await w.WriteElementStringAsync(null, "ServerEncrypted", null, "true");
                    await w.WriteEndElementAsync(); // Properties
                    await w.WriteEndElementAsync(); // Blob
                    break;

                case ListEntry.CommonPrefix p:
                    await w.WriteStartElementAsync(null, "BlobPrefix", null);
                    await w.WriteElementStringAsync(null, "Name", null, p.Key);
                    await w.WriteEndElementAsync(); // BlobPrefix
                    break;
            }
        }
        await w.WriteEndElementAsync(); // Blobs

        await w.WriteElementStringAsync(null, "NextMarker", null, page.NextContinuationToken ?? "");

        await w.WriteEndElementAsync(); // EnumerationResults
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteServicePropertiesAsync(Stream output, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, AzureXmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "StorageServiceProperties", null);

        // Logging
        await w.WriteStartElementAsync(null, "Logging", null);
        await w.WriteElementStringAsync(null, "Version", null, "1.0");
        await w.WriteElementStringAsync(null, "Delete", null, "false");
        await w.WriteElementStringAsync(null, "Read", null, "false");
        await w.WriteElementStringAsync(null, "Write", null, "false");
        await w.WriteStartElementAsync(null, "RetentionPolicy", null);
        await w.WriteElementStringAsync(null, "Enabled", null, "false");
        await w.WriteEndElementAsync(); // RetentionPolicy
        await w.WriteEndElementAsync(); // Logging

        // HourMetrics
        await w.WriteStartElementAsync(null, "HourMetrics", null);
        await w.WriteElementStringAsync(null, "Version", null, "1.0");
        await w.WriteElementStringAsync(null, "Enabled", null, "false");
        await w.WriteStartElementAsync(null, "RetentionPolicy", null);
        await w.WriteElementStringAsync(null, "Enabled", null, "false");
        await w.WriteEndElementAsync(); // RetentionPolicy
        await w.WriteEndElementAsync(); // HourMetrics

        // MinuteMetrics
        await w.WriteStartElementAsync(null, "MinuteMetrics", null);
        await w.WriteElementStringAsync(null, "Version", null, "1.0");
        await w.WriteElementStringAsync(null, "Enabled", null, "false");
        await w.WriteStartElementAsync(null, "RetentionPolicy", null);
        await w.WriteElementStringAsync(null, "Enabled", null, "false");
        await w.WriteEndElementAsync(); // RetentionPolicy
        await w.WriteEndElementAsync(); // MinuteMetrics

        // Cors
        await w.WriteStartElementAsync(null, "Cors", null);
        await w.WriteEndElementAsync(); // Cors

        await w.WriteEndElementAsync(); // StorageServiceProperties
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }
}
