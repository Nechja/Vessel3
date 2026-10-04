namespace Vessel3.Server.S3;

internal sealed class S3XmlWriter(
    IBucketXmlWriter bucketWriter,
    IObjectXmlWriter objectWriter,
    IS3ErrorXmlWriter errorWriter) : IS3XmlWriter
{
    public S3XmlWriter() : this(new BucketXmlWriter(), new ObjectXmlWriter(), new S3ErrorXmlWriter())
    {
    }

    public Task WriteListBuckets(Stream output, IEnumerable<BucketInfo> buckets, CancellationToken ct) =>
        bucketWriter.WriteListBuckets(output, buckets, ct);

    public Task WriteLocationConstraint(Stream output, string region, CancellationToken ct) =>
        bucketWriter.WriteLocationConstraint(output, region, ct);

    public Task WriteVersioningConfiguration(Stream output, VersioningStatus status, CancellationToken ct) =>
        bucketWriter.WriteVersioningConfiguration(output, status, ct);

    public Task WriteWebsiteConfiguration(Stream output, WebsiteConfig cfg, CancellationToken ct) =>
        bucketWriter.WriteWebsiteConfiguration(output, cfg, ct);

    public Task WriteCorsConfiguration(Stream output, CorsConfig cfg, CancellationToken ct) =>
        bucketWriter.WriteCorsConfiguration(output, cfg, ct);

    public Task WriteAccessControlPolicy(Stream output, string ownerId, bool publicRead, CancellationToken ct) =>
        bucketWriter.WriteAccessControlPolicy(output, ownerId, publicRead, ct);

    public Task WriteObjectLockConfiguration(Stream output, ObjectLockConfig cfg, CancellationToken ct) =>
        bucketWriter.WriteObjectLockConfiguration(output, cfg, ct);

    public Task WriteLifecycleConfiguration(Stream output, LifecycleConfig cfg, CancellationToken ct) =>
        bucketWriter.WriteLifecycleConfiguration(output, cfg, ct);

    public Task WriteListMultipartUploads(Stream output, string bucket, IEnumerable<InProgressUpload> uploads, CancellationToken ct) =>
        bucketWriter.WriteListMultipartUploads(output, bucket, uploads, ct);

    public Task WriteListObjects(Stream output, S3ListObjectsRequest req, ListPage page, CancellationToken ct) =>
        objectWriter.WriteListObjects(output, req, page, ct);

    public Task WriteListVersions(Stream output, string bucket, string? prefix, IReadOnlyList<AllVersionsEntry> entries, bool isTruncated, int maxKeys, string? encodingType, CancellationToken ct) =>
        objectWriter.WriteListVersions(output, bucket, prefix, entries, isTruncated, maxKeys, encodingType, ct);

    public Task WriteInitiateMultipartUploadResult(Stream output, string bucket, string key, string uploadId, CancellationToken ct) =>
        objectWriter.WriteInitiateMultipartUploadResult(output, bucket, key, uploadId, ct);

    public Task WriteCompleteMultipartUploadResult(Stream output, string bucket, string key, string etag, ChecksumSet objectChecksums, int partsCount, CancellationToken ct) =>
        objectWriter.WriteCompleteMultipartUploadResult(output, bucket, key, etag, objectChecksums, partsCount, ct);

    public Task WriteListParts(Stream output, string bucket, string key, string uploadId, IReadOnlyList<ListedPart> parts, CancellationToken ct) =>
        objectWriter.WriteListParts(output, bucket, key, uploadId, parts, ct);

    public Task WriteCopyObjectResult(Stream output, CopyOutcome outcome, CancellationToken ct) =>
        objectWriter.WriteCopyObjectResult(output, outcome, ct);

    public Task WriteCopyPartResult(Stream output, string etag, DateTimeOffset lastModified, CancellationToken ct) =>
        objectWriter.WriteCopyPartResult(output, etag, lastModified, ct);

    public Task WriteBatchDeleteResult(Stream output, IEnumerable<BatchDeleteOutcome> outcomes, bool quiet, CancellationToken ct) =>
        objectWriter.WriteBatchDeleteResult(output, outcomes, quiet, ct);

    public Task WriteObjectAttributes(Stream output, ObjectAttributesRequest req, CancellationToken ct) =>
        objectWriter.WriteObjectAttributes(output, req, ct);

    public Task WriteTagging(Stream output, IReadOnlyDictionary<string, string> tags, CancellationToken ct) =>
        objectWriter.WriteTagging(output, tags, ct);

    public Task WriteRetention(Stream output, Retention retention, CancellationToken ct) =>
        objectWriter.WriteRetention(output, retention, ct);

    public Task WriteLegalHold(Stream output, bool on, CancellationToken ct) =>
        objectWriter.WriteLegalHold(output, on, ct);

    public Task WriteError(Stream output, Error error, string resource, string requestId, CancellationToken ct) =>
        errorWriter.WriteError(output, error, resource, requestId, ct);
}
