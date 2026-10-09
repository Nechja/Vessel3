using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;

namespace Vessel3.Server.S3;

internal sealed class ObjectXmlWriter : IObjectXmlWriter
{
    public async Task WriteListObjects(Stream output, S3ListObjectsRequest request, ListPage page, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, S3XmlDefaults.WriterSettings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement(null, "ListBucketResult", S3XmlDefaults.S3Namespace);

            var urlEncode = S3XmlDefaults.IsUrlEncoding(request.EncodingType);

            writer.WriteElementString(null, "Name", null, request.Bucket);
            writer.WriteElementString(null, "Prefix", null, S3XmlDefaults.Encode(request.Prefix ?? "", urlEncode));
            if (request.IsV1) writer.WriteElementString(null, "Marker", null, S3XmlDefaults.Encode(request.Marker ?? "", urlEncode));
            if (request.Delimiter is not null) writer.WriteElementString(null, "Delimiter", null, S3XmlDefaults.Encode(request.Delimiter, urlEncode));
            writer.WriteElementString(null, "MaxKeys", null,
                request.MaxKeys.ToString(CultureInfo.InvariantCulture));
            if (urlEncode) writer.WriteElementString(null, "EncodingType", null, "url");
            if (!request.IsV1)
                writer.WriteElementString(null, "KeyCount", null,
                    page.KeyCount.ToString(CultureInfo.InvariantCulture));
            if (!request.IsV1 && request.StartAfter is not null)
                writer.WriteElementString(null, "StartAfter", null, S3XmlDefaults.Encode(request.StartAfter, urlEncode));
            writer.WriteElementString(null, "IsTruncated", null, page.IsTruncated ? "true" : "false");

            if (request.IsV1 && page.IsTruncated && page.LastKey is not null)
                writer.WriteElementString(null, "NextMarker", null, S3XmlDefaults.Encode(page.LastKey, urlEncode));
            if (!request.IsV1 && page.NextContinuationToken is not null)
                writer.WriteElementString(null, "NextContinuationToken", null, page.NextContinuationToken);

            foreach (var entry in page.Entries)
            {
                ct.ThrowIfCancellationRequested();
                switch (entry)
                {
                    case ListEntry.Contents contents:
                        WriteContents(writer, contents, urlEncode);
                        break;
                    case ListEntry.CommonPrefix commonPrefix:
                        WriteCommonPrefix(writer, commonPrefix, urlEncode);
                        break;
                }
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
        }

        await output.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), ct);
    }

    public async Task WriteListVersions(Stream output, string bucket, string? prefix, IReadOnlyList<AllVersionsEntry> entries, bool isTruncated, int maxKeys, string? encodingType, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, S3XmlDefaults.WriterSettings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement(null, "ListVersionsResult", S3XmlDefaults.S3Namespace);
            var urlEncode = S3XmlDefaults.IsUrlEncoding(encodingType);
            writer.WriteElementString(null, "Name", null, bucket);
            writer.WriteElementString(null, "Prefix", null, S3XmlDefaults.Encode(prefix ?? "", urlEncode));
            writer.WriteElementString(null, "MaxKeys", null, maxKeys.ToString(CultureInfo.InvariantCulture));
            if (urlEncode) writer.WriteElementString(null, "EncodingType", null, "url");
            writer.WriteElementString(null, "IsTruncated", null, isTruncated ? "true" : "false");
            if (isTruncated && entries.Count > 0)
            {
                writer.WriteElementString(null, "NextKeyMarker", null, S3XmlDefaults.Encode(entries[^1].Key, urlEncode));
                writer.WriteElementString(null, "NextVersionIdMarker", null, entries[^1].VersionId);
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                switch (entry)
                {
                    case AllVersionsEntry.Put putEntry:
                        WriteVersionEntry(writer, putEntry, urlEncode);
                        break;
                    case AllVersionsEntry.Marker markerEntry:
                        WriteDeleteMarkerEntry(writer, markerEntry, urlEncode);
                        break;
                }
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
        }

        await output.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), ct);
    }

    public Task WriteInitiateMultipartUploadResult(Stream output, string bucket, string key, string uploadId, CancellationToken ct)
    {
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><InitiateMultipartUploadResult xmlns="{S3XmlDefaults.S3Namespace}"><Bucket>{SecurityElement.Escape(bucket)}</Bucket><Key>{SecurityElement.Escape(key)}</Key><UploadId>{SecurityElement.Escape(uploadId)}</UploadId></InitiateMultipartUploadResult>""";
        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
    }

    public Task WriteCompleteMultipartUploadResult(Stream output, string bucket, string key, string etag, ChecksumSet objectChecksums, int partsCount, CancellationToken ct)
    {
        var suffix = $"-{partsCount.ToString(CultureInfo.InvariantCulture)}";
        var crc32Xml = objectChecksums.Crc32 is { } crc32 ? $"<ChecksumCRC32>{ChecksumAlgorithms.HexToBase64(crc32) + suffix}</ChecksumCRC32>" : "";
        var crc32CXml = objectChecksums.Crc32C is { } crc32C ? $"<ChecksumCRC32C>{ChecksumAlgorithms.HexToBase64(crc32C) + suffix}</ChecksumCRC32C>" : "";
        var sha1Xml = objectChecksums.Sha1 is { } sha1 ? $"<ChecksumSHA1>{ChecksumAlgorithms.HexToBase64(sha1) + suffix}</ChecksumSHA1>" : "";
        var sha256Xml = objectChecksums.Sha256 is { } sha256 ? $"<ChecksumSHA256>{ChecksumAlgorithms.HexToBase64(sha256) + suffix}</ChecksumSHA256>" : "";
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><CompleteMultipartUploadResult xmlns="{S3XmlDefaults.S3Namespace}"><Location>/{SecurityElement.Escape(bucket)}/{SecurityElement.Escape(key)}</Location><Bucket>{SecurityElement.Escape(bucket)}</Bucket><Key>{SecurityElement.Escape(key)}</Key><ETag>"{SecurityElement.Escape(etag)}"</ETag>{crc32Xml}{crc32CXml}{sha1Xml}{sha256Xml}</CompleteMultipartUploadResult>""";
        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
    }

    public async Task WriteListParts(Stream output, string bucket, string key, string uploadId, IReadOnlyList<ListedPart> parts, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, S3XmlDefaults.WriterSettings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement(null, "ListPartsResult", S3XmlDefaults.S3Namespace);
            writer.WriteElementString(null, "Bucket", null, bucket);
            writer.WriteElementString(null, "Key", null, key);
            writer.WriteElementString(null, "UploadId", null, uploadId);
            writer.WriteElementString(null, "StorageClass", null, "STANDARD");
            writer.WriteElementString(null, "IsTruncated", null, "false");

            foreach (var part in parts)
            {
                ct.ThrowIfCancellationRequested();
                writer.WriteStartElement(null, "Part", null);
                writer.WriteElementString(null, "PartNumber", null,
                    part.Number.ToString(CultureInfo.InvariantCulture));
                writer.WriteElementString(null, "LastModified", null,
                    part.LastModified.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
                writer.WriteElementString(null, "ETag", null, $"\"{part.Etag}\"");
                writer.WriteElementString(null, "Size", null, part.Size.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
        }

        await output.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), ct);
    }

    public Task WriteCopyObjectResult(Stream output, CopyOutcome outcome, CancellationToken ct)
    {
        var lastModified = outcome.LastModified.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture);
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><CopyObjectResult xmlns="{S3XmlDefaults.S3Namespace}"><LastModified>{lastModified}</LastModified><ETag>"{SecurityElement.Escape(outcome.Etag)}"</ETag></CopyObjectResult>""";
        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
    }

    public Task WriteCopyPartResult(Stream output, string etag, DateTimeOffset lastModified, CancellationToken ct)
    {
        var lastModStr = lastModified.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture);
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><CopyPartResult xmlns="{S3XmlDefaults.S3Namespace}"><LastModified>{lastModStr}</LastModified><ETag>"{SecurityElement.Escape(etag)}"</ETag></CopyPartResult>""";
        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
    }

    public async Task WriteBatchDeleteResult(Stream output, IEnumerable<BatchDeleteOutcome> outcomes, bool quiet, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, S3XmlDefaults.WriterSettings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement(null, "DeleteResult", S3XmlDefaults.S3Namespace);

            foreach (var outcome in outcomes)
            {
                ct.ThrowIfCancellationRequested();
                if (outcome.Error is null)
                {
                    if (quiet) continue;
                    writer.WriteStartElement(null, "Deleted", null);
                    writer.WriteElementString(null, "Key", null, outcome.Key);
                    if (outcome.VersionId is not null)
                        writer.WriteElementString(null, "VersionId", null, outcome.VersionId);
                    writer.WriteEndElement();
                }
                else
                {
                    writer.WriteStartElement(null, "Error", null);
                    writer.WriteElementString(null, "Key", null, outcome.Key);
                    writer.WriteElementString(null, "Code", null, outcome.Error.Code);
                    writer.WriteElementString(null, "Message", null, outcome.Error.Message);
                    writer.WriteEndElement();
                }
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
        }

        await output.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), ct);
    }

    public async Task WriteObjectAttributes(Stream output, ObjectAttributesRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, S3XmlDefaults.WriterSettings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement(null, "GetObjectAttributesOutput", S3XmlDefaults.S3Namespace);

            if (request.WantEtag && request.Etag is not null)
                writer.WriteElementString(null, "ETag", null, request.Etag);

            if (request.WantChecksum && !string.IsNullOrEmpty(request.ChecksumSha256Base64))
            {
                writer.WriteStartElement(null, "Checksum", null);
                writer.WriteElementString(null, "ChecksumSHA256", null, request.ChecksumSha256Base64);
                writer.WriteEndElement();
            }

            if (request.WantObjectParts && request.Parts is { Count: > 0 } parts)
            {
                writer.WriteStartElement(null, "ObjectParts", null);
                writer.WriteElementString(null, "PartsCount", null,
                    parts.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var part in parts)
                {
                    ct.ThrowIfCancellationRequested();
                    writer.WriteStartElement(null, "Part", null);
                    writer.WriteElementString(null, "PartNumber", null,
                        part.Number.ToString(CultureInfo.InvariantCulture));
                    writer.WriteElementString(null, "Size", null,
                        part.Size.ToString(CultureInfo.InvariantCulture));
                    if (!string.IsNullOrEmpty(part.BlobSha))
                        writer.WriteElementString(null, "ChecksumSHA256", null,
                            Convert.ToBase64String(Convert.FromHexString(part.BlobSha)));
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            if (request.WantStorageClass)
                writer.WriteElementString(null, "StorageClass", null, "STANDARD");

            if (request.WantObjectSize)
                writer.WriteElementString(null, "ObjectSize", null,
                    request.Size.ToString(CultureInfo.InvariantCulture));

            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
        }

        await output.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), ct);
    }

    public Task WriteTagging(Stream output, IReadOnlyDictionary<string, string> tags, CancellationToken ct)
    {
        var tagsXml = string.Concat(tags.Select(tag => $"<Tag><Key>{SecurityElement.Escape(tag.Key)}</Key><Value>{SecurityElement.Escape(tag.Value)}</Value></Tag>"));
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><Tagging xmlns="{S3XmlDefaults.S3Namespace}"><TagSet>{tagsXml}</TagSet></Tagging>""";
        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
    }

    public Task WriteRetention(Stream output, Retention retention, CancellationToken ct)
    {
        var mode = S3XmlDefaults.ModeToWire(retention.Mode);
        var date = retention.RetainUntilDate.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture);
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><Retention xmlns="{S3XmlDefaults.S3Namespace}"><Mode>{mode}</Mode><RetainUntilDate>{date}</RetainUntilDate></Retention>""";
        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
    }

    public Task WriteLegalHold(Stream output, bool on, CancellationToken ct)
    {
        var status = on ? "ON" : "OFF";
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><LegalHold xmlns="{S3XmlDefaults.S3Namespace}"><Status>{status}</Status></LegalHold>""";
        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
    }

    private static void WriteContents(XmlWriter writer, ListEntry.Contents contents, bool urlEncode)
    {
        writer.WriteStartElement(null, "Contents", null);
        writer.WriteElementString(null, "Key", null, S3XmlDefaults.Encode(contents.Key, urlEncode));
        writer.WriteElementString(null, "LastModified", null,
            contents.LastModified.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
        writer.WriteElementString(null, "ETag", null, $"\"{contents.Etag}\"");
        writer.WriteElementString(null, "Size", null, contents.Size.ToString(CultureInfo.InvariantCulture));
        writer.WriteElementString(null, "StorageClass", null, "STANDARD");
        writer.WriteEndElement();
    }

    private static void WriteCommonPrefix(XmlWriter writer, ListEntry.CommonPrefix commonPrefix, bool urlEncode)
    {
        writer.WriteStartElement(null, "CommonPrefixes", null);
        writer.WriteElementString(null, "Prefix", null, S3XmlDefaults.Encode(commonPrefix.Key, urlEncode));
        writer.WriteEndElement();
    }

    private static void WriteVersionEntry(XmlWriter writer, AllVersionsEntry.Put putEntry, bool urlEncode)
    {
        writer.WriteStartElement(null, "Version", null);
        writer.WriteElementString(null, "Key", null, S3XmlDefaults.Encode(putEntry.Key, urlEncode));
        writer.WriteElementString(null, "VersionId", null, putEntry.VersionId);
        writer.WriteElementString(null, "IsLatest", null, putEntry.IsLatest ? "true" : "false");
        writer.WriteElementString(null, "LastModified", null,
            putEntry.At.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
        writer.WriteElementString(null, "ETag", null, $"\"{putEntry.WireEtag}\"");
        writer.WriteElementString(null, "Size", null, putEntry.Size.ToString(CultureInfo.InvariantCulture));
        writer.WriteElementString(null, "StorageClass", null, "STANDARD");
        writer.WriteEndElement();
    }

    private static void WriteDeleteMarkerEntry(XmlWriter writer, AllVersionsEntry.Marker markerEntry, bool urlEncode)
    {
        writer.WriteStartElement(null, "DeleteMarker", null);
        writer.WriteElementString(null, "Key", null, S3XmlDefaults.Encode(markerEntry.Key, urlEncode));
        writer.WriteElementString(null, "VersionId", null, markerEntry.VersionId);
        writer.WriteElementString(null, "IsLatest", null, markerEntry.IsLatest ? "true" : "false");
        writer.WriteElementString(null, "LastModified", null,
            markerEntry.At.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
        writer.WriteEndElement();
    }
}
