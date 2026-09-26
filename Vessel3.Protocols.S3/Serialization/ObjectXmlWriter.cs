using System.Globalization;
using System.Xml;
using Vessel3.Storage;

namespace Vessel3.Server.S3;

internal sealed class ObjectXmlWriter : IObjectXmlWriter
{
    public async Task WriteListObjects(Stream output, ListRequest req, ListPage page, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "ListBucketResult", S3XmlDefaults.S3Namespace);

        var urlEncode = S3XmlDefaults.IsUrlEncoding(req.EncodingType);

        await w.WriteElementStringAsync(null, "Name", null, req.Bucket);
        await w.WriteElementStringAsync(null, "Prefix", null, S3XmlDefaults.Encode(req.Prefix ?? "", urlEncode));
        if (req.IsV1) await w.WriteElementStringAsync(null, "Marker", null, S3XmlDefaults.Encode(req.Marker ?? "", urlEncode));
        if (req.Delimiter is not null) await w.WriteElementStringAsync(null, "Delimiter", null, S3XmlDefaults.Encode(req.Delimiter, urlEncode));
        await w.WriteElementStringAsync(null, "MaxKeys", null,
            req.MaxKeys.ToString(CultureInfo.InvariantCulture));
        if (urlEncode) await w.WriteElementStringAsync(null, "EncodingType", null, "url");
        if (!req.IsV1)
            await w.WriteElementStringAsync(null, "KeyCount", null,
                page.KeyCount.ToString(CultureInfo.InvariantCulture));
        if (!req.IsV1 && req.StartAfter is not null)
            await w.WriteElementStringAsync(null, "StartAfter", null, S3XmlDefaults.Encode(req.StartAfter, urlEncode));
        await w.WriteElementStringAsync(null, "IsTruncated", null, page.IsTruncated ? "true" : "false");

        if (req.IsV1 && page.IsTruncated && page.LastKey is not null)
            await w.WriteElementStringAsync(null, "NextMarker", null, S3XmlDefaults.Encode(page.LastKey, urlEncode));
        if (!req.IsV1 && page.NextContinuationToken is not null)
            await w.WriteElementStringAsync(null, "NextContinuationToken", null, page.NextContinuationToken);

        foreach (var entry in page.Entries)
        {
            ct.ThrowIfCancellationRequested();
            await (entry switch
            {
                ListEntry.Contents c => WriteContents(w, c, urlEncode),
                ListEntry.CommonPrefix cp => WriteCommonPrefix(w, cp, urlEncode),
                _ => Task.CompletedTask,
            });
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteListVersions(Stream output, string bucket, string? prefix, IReadOnlyList<AllVersionsEntry> entries, bool isTruncated, int maxKeys, string? encodingType, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "ListVersionsResult", S3XmlDefaults.S3Namespace);
        var urlEncode = S3XmlDefaults.IsUrlEncoding(encodingType);
        await w.WriteElementStringAsync(null, "Name", null, bucket);
        await w.WriteElementStringAsync(null, "Prefix", null, S3XmlDefaults.Encode(prefix ?? "", urlEncode));
        await w.WriteElementStringAsync(null, "MaxKeys", null, maxKeys.ToString(CultureInfo.InvariantCulture));
        if (urlEncode) await w.WriteElementStringAsync(null, "EncodingType", null, "url");
        await w.WriteElementStringAsync(null, "IsTruncated", null, isTruncated ? "true" : "false");
        if (isTruncated && entries.Count > 0)
        {
            await w.WriteElementStringAsync(null, "NextKeyMarker", null, S3XmlDefaults.Encode(entries[^1].Key, urlEncode));
            await w.WriteElementStringAsync(null, "NextVersionIdMarker", null, entries[^1].VersionId);
        }

        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            await (e switch
            {
                AllVersionsEntry.Put p => WriteVersionEntry(w, p, urlEncode),
                AllVersionsEntry.Marker m => WriteDeleteMarkerEntry(w, m, urlEncode),
                _ => Task.CompletedTask,
            });
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteInitiateMultipartUploadResult(Stream output, string bucket, string key, string uploadId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "InitiateMultipartUploadResult", S3XmlDefaults.S3Namespace);
        await w.WriteElementStringAsync(null, "Bucket", null, bucket);
        await w.WriteElementStringAsync(null, "Key", null, key);
        await w.WriteElementStringAsync(null, "UploadId", null, uploadId);
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteCompleteMultipartUploadResult(Stream output, string bucket, string key, string etag, ChecksumSet objectChecksums, int partsCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "CompleteMultipartUploadResult", S3XmlDefaults.S3Namespace);
        await w.WriteElementStringAsync(null, "Location", null, $"/{bucket}/{key}");
        await w.WriteElementStringAsync(null, "Bucket", null, bucket);
        await w.WriteElementStringAsync(null, "Key", null, key);
        await w.WriteElementStringAsync(null, "ETag", null, $"\"{etag}\"");
        var suffix = $"-{partsCount.ToString(CultureInfo.InvariantCulture)}";
        if (objectChecksums.Crc32 is { } c32)
            await w.WriteElementStringAsync(null, "ChecksumCRC32", null, ChecksumAlgorithms.HexToBase64(c32) + suffix);
        if (objectChecksums.Crc32C is { } c32c)
            await w.WriteElementStringAsync(null, "ChecksumCRC32C", null, ChecksumAlgorithms.HexToBase64(c32c) + suffix);
        if (objectChecksums.Sha1 is { } s1)
            await w.WriteElementStringAsync(null, "ChecksumSHA1", null, ChecksumAlgorithms.HexToBase64(s1) + suffix);
        if (objectChecksums.Sha256 is { } s256)
            await w.WriteElementStringAsync(null, "ChecksumSHA256", null, ChecksumAlgorithms.HexToBase64(s256) + suffix);
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteListParts(Stream output, string bucket, string key, string uploadId, IReadOnlyList<ListedPart> parts, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "ListPartsResult", S3XmlDefaults.S3Namespace);
        await w.WriteElementStringAsync(null, "Bucket", null, bucket);
        await w.WriteElementStringAsync(null, "Key", null, key);
        await w.WriteElementStringAsync(null, "UploadId", null, uploadId);
        await w.WriteElementStringAsync(null, "StorageClass", null, "STANDARD");
        await w.WriteElementStringAsync(null, "IsTruncated", null, "false");

        foreach (var p in parts)
        {
            ct.ThrowIfCancellationRequested();
            await w.WriteStartElementAsync(null, "Part", null);
            await w.WriteElementStringAsync(null, "PartNumber", null,
                p.Number.ToString(CultureInfo.InvariantCulture));
            await w.WriteElementStringAsync(null, "LastModified", null,
                p.LastModified.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
            await w.WriteElementStringAsync(null, "ETag", null, $"\"{p.Etag}\"");
            await w.WriteElementStringAsync(null, "Size", null, p.Size.ToString(CultureInfo.InvariantCulture));
            await w.WriteEndElementAsync();
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteCopyObjectResult(Stream output, CopyOutcome outcome, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "CopyObjectResult", S3XmlDefaults.S3Namespace);
        await w.WriteElementStringAsync(null, "LastModified", null,
            outcome.LastModified.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
        await w.WriteElementStringAsync(null, "ETag", null, $"\"{outcome.Etag}\"");
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteCopyPartResult(Stream output, string etag, DateTimeOffset lastModified, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "CopyPartResult", S3XmlDefaults.S3Namespace);
        await w.WriteElementStringAsync(null, "LastModified", null,
            lastModified.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
        await w.WriteElementStringAsync(null, "ETag", null, $"\"{etag}\"");
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteBatchDeleteResult(Stream output, IEnumerable<BatchDeleteOutcome> outcomes, bool quiet, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "DeleteResult", S3XmlDefaults.S3Namespace);

        foreach (var outcome in outcomes)
        {
            ct.ThrowIfCancellationRequested();
            if (outcome.Error is null)
            {
                if (quiet) continue;
                await w.WriteStartElementAsync(null, "Deleted", null);
                await w.WriteElementStringAsync(null, "Key", null, outcome.Key);
                if (outcome.VersionId is not null)
                    await w.WriteElementStringAsync(null, "VersionId", null, outcome.VersionId);
                await w.WriteEndElementAsync();
            }
            else
            {
                await w.WriteStartElementAsync(null, "Error", null);
                await w.WriteElementStringAsync(null, "Key", null, outcome.Key);
                await w.WriteElementStringAsync(null, "Code", null, outcome.Error.Code);
                await w.WriteElementStringAsync(null, "Message", null, outcome.Error.Message);
                await w.WriteEndElementAsync();
            }
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteObjectAttributes(Stream output, ObjectAttributesRequest req, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "GetObjectAttributesOutput", S3XmlDefaults.S3Namespace);

        if (req.WantEtag && req.Etag is not null)
            await w.WriteElementStringAsync(null, "ETag", null, req.Etag);

        if (req.WantChecksum && !string.IsNullOrEmpty(req.ChecksumSha256Base64))
        {
            await w.WriteStartElementAsync(null, "Checksum", null);
            await w.WriteElementStringAsync(null, "ChecksumSHA256", null, req.ChecksumSha256Base64);
            await w.WriteEndElementAsync();
        }

        if (req.WantObjectParts && req.Parts is { Count: > 0 } parts)
        {
            await w.WriteStartElementAsync(null, "ObjectParts", null);
            await w.WriteElementStringAsync(null, "PartsCount", null,
                parts.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var p in parts)
            {
                ct.ThrowIfCancellationRequested();
                await w.WriteStartElementAsync(null, "Part", null);
                await w.WriteElementStringAsync(null, "PartNumber", null,
                    p.Number.ToString(CultureInfo.InvariantCulture));
                await w.WriteElementStringAsync(null, "Size", null,
                    p.Size.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(p.BlobSha))
                    await w.WriteElementStringAsync(null, "ChecksumSHA256", null,
                        Convert.ToBase64String(Convert.FromHexString(p.BlobSha)));
                await w.WriteEndElementAsync();
            }
            await w.WriteEndElementAsync();
        }

        if (req.WantStorageClass)
            await w.WriteElementStringAsync(null, "StorageClass", null, "STANDARD");

        if (req.WantObjectSize)
            await w.WriteElementStringAsync(null, "ObjectSize", null,
                req.Size.ToString(CultureInfo.InvariantCulture));

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteTagging(Stream output, IReadOnlyDictionary<string, string> tags, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "Tagging", S3XmlDefaults.S3Namespace);
        await w.WriteStartElementAsync(null, "TagSet", null);
        foreach (var (k, v) in tags)
        {
            await w.WriteStartElementAsync(null, "Tag", null);
            await w.WriteElementStringAsync(null, "Key", null, k);
            await w.WriteElementStringAsync(null, "Value", null, v);
            await w.WriteEndElementAsync();
        }
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteRetention(Stream output, Retention retention, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "Retention", S3XmlDefaults.S3Namespace);
        await w.WriteElementStringAsync(null, "Mode", null, S3XmlDefaults.ModeToWire(retention.Mode));
        await w.WriteElementStringAsync(null, "RetainUntilDate", null,
            retention.RetainUntilDate.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteLegalHold(Stream output, bool on, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "LegalHold", S3XmlDefaults.S3Namespace);
        await w.WriteElementStringAsync(null, "Status", null, on ? "ON" : "OFF");
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    private static async Task WriteContents(XmlWriter w, ListEntry.Contents c, bool urlEncode)
    {
        await w.WriteStartElementAsync(null, "Contents", null);
        await w.WriteElementStringAsync(null, "Key", null, S3XmlDefaults.Encode(c.Key, urlEncode));
        await w.WriteElementStringAsync(null, "LastModified", null,
            c.LastModified.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
        await w.WriteElementStringAsync(null, "ETag", null, $"\"{c.Etag}\"");
        await w.WriteElementStringAsync(null, "Size", null, c.Size.ToString(CultureInfo.InvariantCulture));
        await w.WriteElementStringAsync(null, "StorageClass", null, "STANDARD");
        await w.WriteEndElementAsync();
    }

    private static async Task WriteCommonPrefix(XmlWriter w, ListEntry.CommonPrefix cp, bool urlEncode)
    {
        await w.WriteStartElementAsync(null, "CommonPrefixes", null);
        await w.WriteElementStringAsync(null, "Prefix", null, S3XmlDefaults.Encode(cp.Key, urlEncode));
        await w.WriteEndElementAsync();
    }

    private static async Task WriteVersionEntry(XmlWriter w, AllVersionsEntry.Put p, bool urlEncode)
    {
        await w.WriteStartElementAsync(null, "Version", null);
        await w.WriteElementStringAsync(null, "Key", null, S3XmlDefaults.Encode(p.Key, urlEncode));
        await w.WriteElementStringAsync(null, "VersionId", null, p.VersionId);
        await w.WriteElementStringAsync(null, "IsLatest", null, p.IsLatest ? "true" : "false");
        await w.WriteElementStringAsync(null, "LastModified", null,
            p.At.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
        await w.WriteElementStringAsync(null, "ETag", null, $"\"{p.WireEtag}\"");
        await w.WriteElementStringAsync(null, "Size", null, p.Size.ToString(CultureInfo.InvariantCulture));
        await w.WriteElementStringAsync(null, "StorageClass", null, "STANDARD");
        await w.WriteEndElementAsync();
    }

    private static async Task WriteDeleteMarkerEntry(XmlWriter w, AllVersionsEntry.Marker m, bool urlEncode)
    {
        await w.WriteStartElementAsync(null, "DeleteMarker", null);
        await w.WriteElementStringAsync(null, "Key", null, S3XmlDefaults.Encode(m.Key, urlEncode));
        await w.WriteElementStringAsync(null, "VersionId", null, m.VersionId);
        await w.WriteElementStringAsync(null, "IsLatest", null, m.IsLatest ? "true" : "false");
        await w.WriteElementStringAsync(null, "LastModified", null,
            m.At.UtcDateTime.ToString(S3XmlDefaults.Iso8601Ms, CultureInfo.InvariantCulture));
        await w.WriteEndElementAsync();
    }
}
