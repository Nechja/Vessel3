using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;

namespace Vessel3.Server.S3;

internal sealed class ObjectXmlWriter : IObjectXmlWriter
{
    public async Task WriteListObjects(Stream output, S3ListObjectsRequest request, ListPage page, CancellationToken ct)
    {
        await using var writer = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "ListBucketResult", S3XmlDefaults.S3Namespace);

        var urlEncode = S3XmlDefaults.IsUrlEncoding(request.EncodingType);

        await writer.WriteElementStringAsync(null, "Name", null, request.Bucket);
        await writer.WriteElementStringAsync(null, "Prefix", null, S3XmlDefaults.Encode(request.Prefix ?? "", urlEncode));
        if (request.IsV1) await writer.WriteElementStringAsync(null, "Marker", null, S3XmlDefaults.Encode(request.Marker ?? "", urlEncode));
        if (request.Delimiter is not null) await writer.WriteElementStringAsync(null, "Delimiter", null, S3XmlDefaults.Encode(request.Delimiter, urlEncode));
        await writer.WriteElementStringAsync(null, "MaxKeys", null,
            request.MaxKeys.ToString(CultureInfo.InvariantCulture));
        if (urlEncode) await writer.WriteElementStringAsync(null, "EncodingType", null, "url");
        if (!request.IsV1)
            await writer.WriteElementStringAsync(null, "KeyCount", null,
                page.KeyCount.ToString(CultureInfo.InvariantCulture));
        if (!request.IsV1 && request.StartAfter is not null)
            await writer.WriteElementStringAsync(null, "StartAfter", null, S3XmlDefaults.Encode(request.StartAfter, urlEncode));
        await writer.WriteElementStringAsync(null, "IsTruncated", null, page.IsTruncated ? "true" : "false");

        if (request.IsV1 && page.IsTruncated && page.LastKey is not null)
            await writer.WriteElementStringAsync(null, "NextMarker", null, S3XmlDefaults.Encode(page.LastKey, urlEncode));
        if (!request.IsV1 && page.NextContinuationToken is not null)
            await writer.WriteElementStringAsync(null, "NextContinuationToken", null, page.NextContinuationToken);

        foreach (var entry in page.Entries)
        {
            ct.ThrowIfCancellationRequested();
            await (entry switch
            {
                ListEntry.Contents contents => WriteContents(writer, contents, urlEncode),
                ListEntry.CommonPrefix commonPrefix => WriteCommonPrefix(writer, commonPrefix, urlEncode),
                _ => Task.CompletedTask,
            });
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    public async Task WriteListVersions(Stream output, string bucket, string? prefix, IReadOnlyList<AllVersionsEntry> entries, bool isTruncated, int maxKeys, string? encodingType, CancellationToken ct)
    {
        await using var writer = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "ListVersionsResult", S3XmlDefaults.S3Namespace);
        var urlEncode = S3XmlDefaults.IsUrlEncoding(encodingType);
        await writer.WriteElementStringAsync(null, "Name", null, bucket);
        await writer.WriteElementStringAsync(null, "Prefix", null, S3XmlDefaults.Encode(prefix ?? "", urlEncode));
        await writer.WriteElementStringAsync(null, "MaxKeys", null, maxKeys.ToString(CultureInfo.InvariantCulture));
        if (urlEncode) await writer.WriteElementStringAsync(null, "EncodingType", null, "url");
        await writer.WriteElementStringAsync(null, "IsTruncated", null, isTruncated ? "true" : "false");
        if (isTruncated && entries.Count > 0)
        {
            await writer.WriteElementStringAsync(null, "NextKeyMarker", null, S3XmlDefaults.Encode(entries[^1].Key, urlEncode));
            await writer.WriteElementStringAsync(null, "NextVersionIdMarker", null, entries[^1].VersionId);
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            await (entry switch
            {
                AllVersionsEntry.Put putEntry => WriteVersionEntry(writer, putEntry, urlEncode),
                AllVersionsEntry.Marker markerEntry => WriteDeleteMarkerEntry(writer, markerEntry, urlEncode),
                _ => Task.CompletedTask,
            });
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
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
        await using var writer = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "ListPartsResult", S3XmlDefaults.S3Namespace);
        await writer.WriteElementStringAsync(null, "Bucket", null, bucket);
        await writer.WriteElementStringAsync(null, "Key", null, key);
        await writer.WriteElementStringAsync(null, "UploadId", null, uploadId);
        await writer.WriteElementStringAsync(null, "StorageClass", null, "STANDARD");
        await writer.WriteElementStringAsync(null, "IsTruncated", null, "false");

        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteStartElementAsync(null, "Part", null);
            await writer.WriteElementStringAsync(null, "PartNumber", null,
                part.Number.ToString(CultureInfo.InvariantCulture));
            await writer.WriteElementStringAsync(null, "LastModified", null,
                part.LastModified.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
            await writer.WriteElementStringAsync(null, "ETag", null, $"\"{part.Etag}\"");
            await writer.WriteElementStringAsync(null, "Size", null, part.Size.ToString(CultureInfo.InvariantCulture));
            await writer.WriteEndElementAsync();
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
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
        await using var writer = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "DeleteResult", S3XmlDefaults.S3Namespace);

        foreach (var outcome in outcomes)
        {
            ct.ThrowIfCancellationRequested();
            if (outcome.Error is null)
            {
                if (quiet) continue;
                await writer.WriteStartElementAsync(null, "Deleted", null);
                await writer.WriteElementStringAsync(null, "Key", null, outcome.Key);
                if (outcome.VersionId is not null)
                    await writer.WriteElementStringAsync(null, "VersionId", null, outcome.VersionId);
                await writer.WriteEndElementAsync();
            }
            else
            {
                await writer.WriteStartElementAsync(null, "Error", null);
                await writer.WriteElementStringAsync(null, "Key", null, outcome.Key);
                await writer.WriteElementStringAsync(null, "Code", null, outcome.Error.Code);
                await writer.WriteElementStringAsync(null, "Message", null, outcome.Error.Message);
                await writer.WriteEndElementAsync();
            }
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    public async Task WriteObjectAttributes(Stream output, ObjectAttributesRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var writer = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "GetObjectAttributesOutput", S3XmlDefaults.S3Namespace);

        if (request.WantEtag && request.Etag is not null)
            await writer.WriteElementStringAsync(null, "ETag", null, request.Etag);

        if (request.WantChecksum && !string.IsNullOrEmpty(request.ChecksumSha256Base64))
        {
            await writer.WriteStartElementAsync(null, "Checksum", null);
            await writer.WriteElementStringAsync(null, "ChecksumSHA256", null, request.ChecksumSha256Base64);
            await writer.WriteEndElementAsync();
        }

        if (request.WantObjectParts && request.Parts is { Count: > 0 } parts)
        {
            await writer.WriteStartElementAsync(null, "ObjectParts", null);
            await writer.WriteElementStringAsync(null, "PartsCount", null,
                parts.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var part in parts)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteStartElementAsync(null, "Part", null);
                await writer.WriteElementStringAsync(null, "PartNumber", null,
                    part.Number.ToString(CultureInfo.InvariantCulture));
                await writer.WriteElementStringAsync(null, "Size", null,
                    part.Size.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(part.BlobSha))
                    await writer.WriteElementStringAsync(null, "ChecksumSHA256", null,
                        Convert.ToBase64String(Convert.FromHexString(part.BlobSha)));
                await writer.WriteEndElementAsync();
            }
            await writer.WriteEndElementAsync();
        }

        if (request.WantStorageClass)
            await writer.WriteElementStringAsync(null, "StorageClass", null, "STANDARD");

        if (request.WantObjectSize)
            await writer.WriteElementStringAsync(null, "ObjectSize", null,
                request.Size.ToString(CultureInfo.InvariantCulture));

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
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

    private static async Task WriteContents(XmlWriter writer, ListEntry.Contents contents, bool urlEncode)
    {
        await writer.WriteStartElementAsync(null, "Contents", null);
        await writer.WriteElementStringAsync(null, "Key", null, S3XmlDefaults.Encode(contents.Key, urlEncode));
        await writer.WriteElementStringAsync(null, "LastModified", null,
            contents.LastModified.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
        await writer.WriteElementStringAsync(null, "ETag", null, $"\"{contents.Etag}\"");
        await writer.WriteElementStringAsync(null, "Size", null, contents.Size.ToString(CultureInfo.InvariantCulture));
        await writer.WriteElementStringAsync(null, "StorageClass", null, "STANDARD");
        await writer.WriteEndElementAsync();
    }

    private static async Task WriteCommonPrefix(XmlWriter writer, ListEntry.CommonPrefix commonPrefix, bool urlEncode)
    {
        await writer.WriteStartElementAsync(null, "CommonPrefixes", null);
        await writer.WriteElementStringAsync(null, "Prefix", null, S3XmlDefaults.Encode(commonPrefix.Key, urlEncode));
        await writer.WriteEndElementAsync();
    }

    private static async Task WriteVersionEntry(XmlWriter writer, AllVersionsEntry.Put putEntry, bool urlEncode)
    {
        await writer.WriteStartElementAsync(null, "Version", null);
        await writer.WriteElementStringAsync(null, "Key", null, S3XmlDefaults.Encode(putEntry.Key, urlEncode));
        await writer.WriteElementStringAsync(null, "VersionId", null, putEntry.VersionId);
        await writer.WriteElementStringAsync(null, "IsLatest", null, putEntry.IsLatest ? "true" : "false");
        await writer.WriteElementStringAsync(null, "LastModified", null,
            putEntry.At.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
        await writer.WriteElementStringAsync(null, "ETag", null, $"\"{putEntry.WireEtag}\"");
        await writer.WriteElementStringAsync(null, "Size", null, putEntry.Size.ToString(CultureInfo.InvariantCulture));
        await writer.WriteElementStringAsync(null, "StorageClass", null, "STANDARD");
        await writer.WriteEndElementAsync();
    }

    private static async Task WriteDeleteMarkerEntry(XmlWriter writer, AllVersionsEntry.Marker markerEntry, bool urlEncode)
    {
        await writer.WriteStartElementAsync(null, "DeleteMarker", null);
        await writer.WriteElementStringAsync(null, "Key", null, S3XmlDefaults.Encode(markerEntry.Key, urlEncode));
        await writer.WriteElementStringAsync(null, "VersionId", null, markerEntry.VersionId);
        await writer.WriteElementStringAsync(null, "IsLatest", null, markerEntry.IsLatest ? "true" : "false");
        await writer.WriteElementStringAsync(null, "LastModified", null,
            markerEntry.At.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
        await writer.WriteEndElementAsync();
    }
}
