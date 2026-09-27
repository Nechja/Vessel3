using Vessel3.Storage;

namespace Vessel3.Server.S3;

internal interface IObjectXmlWriter
{
    Task WriteListObjects(Stream output, S3ListObjectsRequest req, ListPage page, CancellationToken ct);
    Task WriteListVersions(Stream output, string bucket, string? prefix, IReadOnlyList<AllVersionsEntry> entries, bool isTruncated, int maxKeys, string? encodingType, CancellationToken ct);
    Task WriteInitiateMultipartUploadResult(Stream output, string bucket, string key, string uploadId, CancellationToken ct);
    Task WriteCompleteMultipartUploadResult(Stream output, string bucket, string key, string etag, ChecksumSet objectChecksums, int partsCount, CancellationToken ct);
    Task WriteListParts(Stream output, string bucket, string key, string uploadId, IReadOnlyList<ListedPart> parts, CancellationToken ct);
    Task WriteCopyObjectResult(Stream output, CopyOutcome outcome, CancellationToken ct);
    Task WriteCopyPartResult(Stream output, string etag, DateTimeOffset lastModified, CancellationToken ct);
    Task WriteBatchDeleteResult(Stream output, IEnumerable<BatchDeleteOutcome> outcomes, bool quiet, CancellationToken ct);
    Task WriteObjectAttributes(Stream output, ObjectAttributesRequest req, CancellationToken ct);
    Task WriteTagging(Stream output, IReadOnlyDictionary<string, string> tags, CancellationToken ct);
    Task WriteRetention(Stream output, Retention retention, CancellationToken ct);
    Task WriteLegalHold(Stream output, bool on, CancellationToken ct);
}
