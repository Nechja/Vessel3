using System.Globalization;
using System.Text;
using System.Xml;
using Vessel3.Server;
using Vessel3.Server.Storage;

namespace Vessel3.Server.S3;

internal interface IS3XmlWriter
{
    Task WriteListBuckets(Stream output, IEnumerable<BucketInfo> buckets, CancellationToken ct);
    Task WriteListObjects(Stream output, ListRequest req, ListPage page, CancellationToken ct);
    Task WriteError(Stream output, Error error, string resource, string requestId, CancellationToken ct);
    Task WriteCopyObjectResult(Stream output, CopyOutcome outcome, CancellationToken ct);
    Task WriteBatchDeleteResult(Stream output, IEnumerable<BatchDeleteOutcome> outcomes, bool quiet, CancellationToken ct);
    Task WriteInitiateMultipartUploadResult(Stream output, string bucket, string key, string uploadId, CancellationToken ct);
    Task WriteCompleteMultipartUploadResult(Stream output, string bucket, string key, string etag, CancellationToken ct);
    Task WriteLocationConstraint(Stream output, string region, CancellationToken ct);
    Task WriteListMultipartUploads(Stream output, string bucket, IEnumerable<InProgressUpload> uploads, CancellationToken ct);
    Task WriteListParts(Stream output, string bucket, string key, string uploadId, IReadOnlyList<ListedPart> parts, CancellationToken ct);
    Task WriteCopyPartResult(Stream output, string etag, DateTimeOffset lastModified, CancellationToken ct);
    Task WriteVersioningConfiguration(Stream output, VersioningStatus status, CancellationToken ct);
    Task WriteListVersions(Stream output, string bucket, string? prefix, IEnumerable<AllVersionsEntry> entries, CancellationToken ct);
}

internal sealed class S3XmlWriter : IS3XmlWriter
{
    private const string S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";
    private const string Iso8601Ms = "yyyy-MM-ddTHH:mm:ss.fffZ";

    private readonly XmlWriterSettings settings = new()
    {
        Async = true,
        Indent = false,
        OmitXmlDeclaration = false,
        Encoding = new UTF8Encoding(false),
    };

    public async Task WriteListBuckets(Stream output, IEnumerable<BucketInfo> buckets, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "ListAllMyBucketsResult", S3Namespace);

        await w.WriteStartElementAsync(null, "Owner", null);
        await w.WriteElementStringAsync(null, "ID", null, "vessel3");
        await w.WriteElementStringAsync(null, "DisplayName", null, "vessel3");
        await w.WriteEndElementAsync();

        await w.WriteStartElementAsync(null, "Buckets", null);
        foreach (var b in buckets)
        {
            ct.ThrowIfCancellationRequested();
            await w.WriteStartElementAsync(null, "Bucket", null);
            await w.WriteElementStringAsync(null, "Name", null, b.Name);
            await w.WriteElementStringAsync(null, "CreationDate", null,
                b.CreatedAt.UtcDateTime.ToString(Iso8601Ms, CultureInfo.InvariantCulture));
            await w.WriteEndElementAsync();
        }
        await w.WriteEndElementAsync();

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteListObjects(Stream output, ListRequest req, ListPage page, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "ListBucketResult", S3Namespace);

        await w.WriteElementStringAsync(null, "Name", null, req.Bucket);
        await w.WriteElementStringAsync(null, "Prefix", null, req.Prefix ?? "");
        if (req.IsV1) await w.WriteElementStringAsync(null, "Marker", null, req.Marker ?? "");
        if (req.Delimiter is not null) await w.WriteElementStringAsync(null, "Delimiter", null, req.Delimiter);
        await w.WriteElementStringAsync(null, "MaxKeys", null,
            req.MaxKeys.ToString(CultureInfo.InvariantCulture));
        if (!req.IsV1)
            await w.WriteElementStringAsync(null, "KeyCount", null,
                page.KeyCount.ToString(CultureInfo.InvariantCulture));
        await w.WriteElementStringAsync(null, "IsTruncated", null, page.IsTruncated ? "true" : "false");

        if (req.IsV1 && page.IsTruncated && page.LastKey is not null)
            await w.WriteElementStringAsync(null, "NextMarker", null, page.LastKey);
        if (!req.IsV1 && page.NextContinuationToken is not null)
            await w.WriteElementStringAsync(null, "NextContinuationToken", null, page.NextContinuationToken);

        foreach (var entry in page.Entries)
        {
            ct.ThrowIfCancellationRequested();
            await (entry switch
            {
                ListEntry.Contents c => WriteContents(w, c),
                ListEntry.CommonPrefix cp => WriteCommonPrefix(w, cp),
                _ => Task.CompletedTask,
            });
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteBatchDeleteResult(Stream output, IEnumerable<BatchDeleteOutcome> outcomes, bool quiet, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "DeleteResult", S3Namespace);

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

    public async Task WriteListVersions(Stream output, string bucket, string? prefix, IEnumerable<AllVersionsEntry> entries, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "ListVersionsResult", S3Namespace);
        await w.WriteElementStringAsync(null, "Name", null, bucket);
        await w.WriteElementStringAsync(null, "Prefix", null, prefix ?? "");
        await w.WriteElementStringAsync(null, "MaxKeys", null, "1000");
        await w.WriteElementStringAsync(null, "IsTruncated", null, "false");

        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            switch (e)
            {
                case AllVersionsEntry.Put p:
                    await w.WriteStartElementAsync(null, "Version", null);
                    await w.WriteElementStringAsync(null, "Key", null, p.Key);
                    await w.WriteElementStringAsync(null, "VersionId", null, p.VersionId);
                    await w.WriteElementStringAsync(null, "IsLatest", null, p.IsLatest ? "true" : "false");
                    await w.WriteElementStringAsync(null, "LastModified", null,
                        p.At.UtcDateTime.ToString(Iso8601Ms, CultureInfo.InvariantCulture));
                    await w.WriteElementStringAsync(null, "ETag", null, $"\"{p.WireEtag}\"");
                    await w.WriteElementStringAsync(null, "Size", null,
                        p.Size.ToString(CultureInfo.InvariantCulture));
                    await w.WriteElementStringAsync(null, "StorageClass", null, "STANDARD");
                    await w.WriteEndElementAsync();
                    break;
                case AllVersionsEntry.Marker m:
                    await w.WriteStartElementAsync(null, "DeleteMarker", null);
                    await w.WriteElementStringAsync(null, "Key", null, m.Key);
                    await w.WriteElementStringAsync(null, "VersionId", null, m.VersionId);
                    await w.WriteElementStringAsync(null, "IsLatest", null, m.IsLatest ? "true" : "false");
                    await w.WriteElementStringAsync(null, "LastModified", null,
                        m.At.UtcDateTime.ToString(Iso8601Ms, CultureInfo.InvariantCulture));
                    await w.WriteEndElementAsync();
                    break;
            }
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteVersioningConfiguration(Stream output, VersioningStatus status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "VersioningConfiguration", S3Namespace);
        if (status is not VersioningStatus.Unversioned)
            await w.WriteElementStringAsync(null, "Status", null, status.ToString());
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteCopyPartResult(Stream output, string etag, DateTimeOffset lastModified, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "CopyPartResult", S3Namespace);
        await w.WriteElementStringAsync(null, "LastModified", null,
            lastModified.UtcDateTime.ToString(Iso8601Ms, CultureInfo.InvariantCulture));
        await w.WriteElementStringAsync(null, "ETag", null, $"\"{etag}\"");
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteCopyObjectResult(Stream output, CopyOutcome outcome, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "CopyObjectResult", S3Namespace);
        await w.WriteElementStringAsync(null, "LastModified", null,
            outcome.LastModified.UtcDateTime.ToString(Iso8601Ms, CultureInfo.InvariantCulture));
        await w.WriteElementStringAsync(null, "ETag", null, $"\"{outcome.Etag}\"");
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteInitiateMultipartUploadResult(Stream output, string bucket, string key, string uploadId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "InitiateMultipartUploadResult", S3Namespace);
        await w.WriteElementStringAsync(null, "Bucket", null, bucket);
        await w.WriteElementStringAsync(null, "Key", null, key);
        await w.WriteElementStringAsync(null, "UploadId", null, uploadId);
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteListMultipartUploads(Stream output, string bucket, IEnumerable<InProgressUpload> uploads, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "ListMultipartUploadsResult", S3Namespace);
        await w.WriteElementStringAsync(null, "Bucket", null, bucket);
        await w.WriteElementStringAsync(null, "IsTruncated", null, "false");

        foreach (var u in uploads)
        {
            ct.ThrowIfCancellationRequested();
            await w.WriteStartElementAsync(null, "Upload", null);
            await w.WriteElementStringAsync(null, "Key", null, u.Key);
            await w.WriteElementStringAsync(null, "UploadId", null, u.UploadId);
            await w.WriteElementStringAsync(null, "Initiated", null,
                u.Initiated.UtcDateTime.ToString(Iso8601Ms, CultureInfo.InvariantCulture));
            await w.WriteElementStringAsync(null, "StorageClass", null, "STANDARD");
            await w.WriteEndElementAsync();
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteListParts(Stream output, string bucket, string key, string uploadId, IReadOnlyList<ListedPart> parts, CancellationToken ct)
    {
        await using var w = XmlWriter.Create(output, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "ListPartsResult", S3Namespace);
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
                p.LastModified.UtcDateTime.ToString(Iso8601Ms, CultureInfo.InvariantCulture));
            await w.WriteElementStringAsync(null, "ETag", null, $"\"{p.Etag}\"");
            await w.WriteElementStringAsync(null, "Size", null, p.Size.ToString(CultureInfo.InvariantCulture));
            await w.WriteEndElementAsync();
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteLocationConstraint(Stream output, string region, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "LocationConstraint", S3Namespace);
        if (region is not "us-east-1") await w.WriteStringAsync(region);
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteCompleteMultipartUploadResult(Stream output, string bucket, string key, string etag, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "CompleteMultipartUploadResult", S3Namespace);
        await w.WriteElementStringAsync(null, "Location", null, $"/{bucket}/{key}");
        await w.WriteElementStringAsync(null, "Bucket", null, bucket);
        await w.WriteElementStringAsync(null, "Key", null, key);
        await w.WriteElementStringAsync(null, "ETag", null, $"\"{etag}\"");
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    public async Task WriteError(Stream output, Error error, string resource, string requestId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "Error", null);
        await w.WriteElementStringAsync(null, "Code", null, error.Code);
        await w.WriteElementStringAsync(null, "Message", null, error.Message);
        await w.WriteElementStringAsync(null, "Resource", null, resource);
        await w.WriteElementStringAsync(null, "RequestId", null, requestId);
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    private async Task WriteContents(XmlWriter w, ListEntry.Contents c)
    {
        await w.WriteStartElementAsync(null, "Contents", null);
        await w.WriteElementStringAsync(null, "Key", null, c.Key);
        await w.WriteElementStringAsync(null, "LastModified", null,
            c.LastModified.UtcDateTime.ToString(Iso8601Ms, CultureInfo.InvariantCulture));
        await w.WriteElementStringAsync(null, "ETag", null, $"\"{c.Etag}\"");
        await w.WriteElementStringAsync(null, "Size", null, c.Size.ToString(CultureInfo.InvariantCulture));
        await w.WriteElementStringAsync(null, "StorageClass", null, "STANDARD");
        await w.WriteEndElementAsync();
    }

    private async Task WriteCommonPrefix(XmlWriter w, ListEntry.CommonPrefix cp)
    {
        await w.WriteStartElementAsync(null, "CommonPrefixes", null);
        await w.WriteElementStringAsync(null, "Prefix", null, cp.Key);
        await w.WriteEndElementAsync();
    }
}
