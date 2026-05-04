using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record PutOutcome(string Etag, string Sha256, string VersionId, long Size);
internal sealed record CopyOutcome(string Etag, DateTimeOffset LastModified, string VersionId);
internal sealed record StoredObject(Stream Body, long Size, DateTimeOffset LastModified, string Etag, string Sha256, string ContentType, IReadOnlyDictionary<string, string> Metadata);
internal sealed record ObjectStat(long Size, DateTimeOffset LastModified, string Etag, string Sha256, string ContentType, IReadOnlyDictionary<string, string> Metadata);

internal interface IObjectStore
{
    Task<Result<PutOutcome>> Put(string bucket, string key, Stream body, long? declaredSize, string? contentType, string? declaredSha256, string? declaredMd5Base64, IReadOnlyDictionary<string, string> metadata, CancellationToken ct);
    Result<CopyOutcome> Copy(string destBucket, string destKey, string srcBucket, string srcKey, IHeaderDictionary copyHeaders, IReadOnlyDictionary<string, string>? metadataOverride);
    Result<StoredObject> Get(string bucket, string key, string? versionId = null);
    Result<ObjectStat> Stat(string bucket, string key, string? versionId = null);
    Result<DeleteOutcome> Delete(string bucket, string key);
    Result<DeleteOutcome> DeleteVersion(string bucket, string key, string versionId);
}

internal sealed class ObjectStore(IBucketRegistry registry, IBlobPool blobs, IPreconditionEvaluator pre) : IObjectStore
{
    public async Task<Result<PutOutcome>> Put(string bucket, string key, Stream body, long? declaredSize, string? contentType, string? declaredSha256, string? declaredMd5Base64, IReadOnlyDictionary<string, string> metadata, CancellationToken ct) =>
        await blobs.Write(body, declaredSize, ct) switch
        {
            Result<StoredBlob>.Failure bf => bf.Error,
            Result<StoredBlob>.Success ok when declaredSha256 is not null
                && !string.Equals(ok.Value.Sha, declaredSha256, StringComparison.OrdinalIgnoreCase)
                => new BadDigestError($"sha256 declared {declaredSha256}, actual {ok.Value.Sha}"),
            Result<StoredBlob>.Success ok when declaredMd5Base64 is not null
                && !string.Equals(declaredMd5Base64, Convert.ToBase64String(Convert.FromHexString(ok.Value.Md5)), StringComparison.Ordinal)
                => new BadDigestError($"md5 declared {declaredMd5Base64}, actual {Convert.ToBase64String(Convert.FromHexString(ok.Value.Md5))}"),
            Result<StoredBlob>.Success ok => RecordPut(bucket, key, ok.Value, contentType, metadata),
            _ => throw new System.Diagnostics.UnreachableException(),
        };

    public Result<StoredObject> Get(string bucket, string key, string? versionId = null) =>
        Lookup(bucket, key, versionId).Match<Result<StoredObject>>(
            put => put is null
                ? new NotFoundError($"{bucket}/{key}")
                : OpenBlob(put),
            err => err);

    public Result<ObjectStat> Stat(string bucket, string key, string? versionId = null) =>
        Lookup(bucket, key, versionId).Match<Result<ObjectStat>>(
            put => put is null
                ? new NotFoundError($"{bucket}/{key}")
                : new ObjectStat(put.Size, put.At, put.WireEtag, put.WireSha256, put.ContentType, put.Metadata),
            err => err);

    private Result<PutEntry?> Lookup(string bucket, string key, string? versionId) =>
        versionId is null
            ? registry.GetCurrentPut(bucket, key)
            : registry.GetVersion(bucket, key, versionId);

    public Result<DeleteOutcome> Delete(string bucket, string key) =>
        registry.AppendDelete(bucket, key);

    public Result<DeleteOutcome> DeleteVersion(string bucket, string key, string versionId) =>
        registry.HardDeleteVersion(bucket, key, versionId);

    public Result<CopyOutcome> Copy(string destBucket, string destKey, string srcBucket, string srcKey, IHeaderDictionary copyHeaders, IReadOnlyDictionary<string, string>? metadataOverride) =>
        registry.GetCurrentPut(srcBucket, srcKey).Match<Result<CopyOutcome>>(
            srcEntry => srcEntry is null
                ? new NotFoundError($"{srcBucket}/{srcKey}")
                : pre.EvaluateCopySource(copyHeaders, srcEntry.Md5, srcEntry.At) is Precondition.Failed
                    ? new PreconditionFailedError($"{srcBucket}/{srcKey}")
                    : registry.AppendPut(destBucket, destKey, new PutRequest(
                            BlobSha: srcEntry.BlobSha,
                            Md5: srcEntry.Md5,
                            Size: srcEntry.Size,
                            ContentType: srcEntry.ContentType,
                            Metadata: metadataOverride ?? srcEntry.Metadata,
                            Parts: srcEntry.Parts))
                        .Match<Result<CopyOutcome>>(
                            written => new CopyOutcome(written.Md5, written.At, written.VersionId),
                            err => err),
            err => err);

    private Result<PutOutcome> RecordPut(string bucket, string key, StoredBlob blob, string? contentType, IReadOnlyDictionary<string, string> metadata)
    {
        var resolved = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType;
        return registry.AppendPut(bucket, key, new PutRequest(
                BlobSha: blob.Sha,
                Md5: blob.Md5,
                Size: blob.Size,
                ContentType: resolved,
                Metadata: metadata)).Match<Result<PutOutcome>>(
            entry => new PutOutcome(blob.Md5, blob.Sha, entry.VersionId, blob.Size),
            err => err);
    }

    private Result<StoredObject> OpenBlob(PutEntry put) =>
        put.Parts is { } parts
            ? new StoredObject(new ConcatStream(parts, blobs), put.Size, put.At, put.WireEtag, "", put.ContentType, put.Metadata)
            : blobs.Open(put.BlobSha).Match<Result<StoredObject>>(
                stream => new StoredObject(stream, put.Size, put.At, put.Md5, put.BlobSha, put.ContentType, put.Metadata),
                err => err);
}
