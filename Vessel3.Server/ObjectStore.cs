using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record PutOutcome(string Etag, string Sha256, string VersionId, long Size);
internal sealed record CopyOutcome(string Etag, DateTimeOffset LastModified, string VersionId);
internal sealed record StoredObject(Stream Body, long Size, DateTimeOffset LastModified, string Etag, string Sha256, string ContentType, IReadOnlyDictionary<string, string> Metadata);
internal sealed record ObjectStat(long Size, DateTimeOffset LastModified, string Etag, string Sha256, string ContentType, IReadOnlyDictionary<string, string> Metadata);

internal interface IObjectStore
{
    Task<Result<PutOutcome>> Put(string bucket, string key, Stream body, long? declaredSize, string? contentType, string? declaredSha256, IReadOnlyDictionary<string, string> metadata, CancellationToken ct);
    Result<CopyOutcome> Copy(string destBucket, string destKey, string srcBucket, string srcKey, IHeaderDictionary copyHeaders, IReadOnlyDictionary<string, string>? metadataOverride);
    Result<StoredObject> Get(string bucket, string key);
    Result<ObjectStat> Stat(string bucket, string key);
    Result<bool> Delete(string bucket, string key);
}

internal sealed class ObjectStore(IBucketRegistry registry, IBlobPool blobs, IPreconditionEvaluator pre) : IObjectStore
{
    public Task<Result<PutOutcome>> Put(string bucket, string key, Stream body, long? declaredSize, string? contentType, string? declaredSha256, IReadOnlyDictionary<string, string> metadata, CancellationToken ct) =>
        !registry.IsValidName(bucket) ? Task.FromResult<Result<PutOutcome>>(new InvalidPathError(bucket))
        : string.IsNullOrEmpty(key) ? Task.FromResult<Result<PutOutcome>>(new InvalidPathError($"{bucket}/{key}"))
        : registry.Open(bucket) is { } b
            ? PutInto(b, key, body, declaredSize, contentType, declaredSha256, metadata, ct)
            : Task.FromResult<Result<PutOutcome>>(new NotFoundError(bucket));

    public Result<StoredObject> Get(string bucket, string key) =>
        !registry.IsValidName(bucket) ? new InvalidPathError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : registry.Open(bucket) is { } b
            ? GetFrom(b, bucket, key)
            : new NotFoundError(bucket);

    public Result<ObjectStat> Stat(string bucket, string key) =>
        !registry.IsValidName(bucket) ? new InvalidPathError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : registry.Open(bucket) is { } b
            ? StatFrom(b, bucket, key)
            : new NotFoundError(bucket);

    public Result<CopyOutcome> Copy(string destBucket, string destKey, string srcBucket, string srcKey, IHeaderDictionary copyHeaders, IReadOnlyDictionary<string, string>? metadataOverride)
    {
        if (!registry.IsValidName(destBucket)) return new InvalidPathError(destBucket);
        if (string.IsNullOrEmpty(destKey)) return new InvalidPathError($"{destBucket}/{destKey}");
        if (!registry.IsValidName(srcBucket)) return new InvalidPathError(srcBucket);
        if (string.IsNullOrEmpty(srcKey)) return new InvalidPathError($"{srcBucket}/{srcKey}");

        var src = registry.Open(srcBucket);
        if (src is null) return new NotFoundError(srcBucket);

        if (src.Index.GetCurrentPut(srcKey) is not Result<PutEntry?>.Success { Value: { } srcEntry })
            return new NotFoundError($"{srcBucket}/{srcKey}");

        var precond = pre.EvaluateCopySource(copyHeaders, srcEntry.Md5, srcEntry.At);
        if (precond is Precondition.Failed) return new PreconditionFailedError($"{srcBucket}/{srcKey}");

        var dest = registry.Open(destBucket);
        if (dest is null) return new NotFoundError(destBucket);

        var meta = metadataOverride ?? srcEntry.Metadata;
        var written = dest.AppendPut(destKey, srcEntry.BlobSha, srcEntry.Md5, srcEntry.Size, srcEntry.ContentType, meta);
        return new CopyOutcome(written.Md5, written.At, written.VersionId);
    }

    public Result<bool> Delete(string bucket, string key) =>
        !registry.IsValidName(bucket) ? new InvalidPathError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : registry.Open(bucket)?.AppendHardDeleteCurrent(key) ?? false;

    private async Task<Result<PutOutcome>> PutInto(Bucket b, string key, Stream body, long? declaredSize, string? contentType, string? declaredSha256, IReadOnlyDictionary<string, string> metadata, CancellationToken ct) =>
        await blobs.Write(body, declaredSize, ct) switch
        {
            Result<StoredBlob>.Failure bf => bf.Error,
            Result<StoredBlob>.Success ok when declaredSha256 is not null
                && !string.Equals(ok.Value.Sha, declaredSha256, StringComparison.OrdinalIgnoreCase)
                => new BadDigestError($"declared {declaredSha256}, actual {ok.Value.Sha}"),
            Result<StoredBlob>.Success ok => RecordPut(b, key, ok.Value, contentType, metadata),
            _ => throw new System.Diagnostics.UnreachableException(),
        };

    private PutOutcome RecordPut(Bucket b, string key, StoredBlob blob, string? contentType, IReadOnlyDictionary<string, string> metadata)
    {
        var resolved = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType;
        var entry = b.AppendPut(key, blob.Sha, blob.Md5, blob.Size, resolved, metadata);
        return new PutOutcome(blob.Md5, blob.Sha, entry.VersionId, blob.Size);
    }

    private Result<StoredObject> GetFrom(Bucket b, string bucket, string key) =>
        b.Index.GetCurrentPut(key) is Result<PutEntry?>.Success { Value: { } put }
            ? OpenBlob(put)
            : new NotFoundError($"{bucket}/{key}");

    private Result<StoredObject> OpenBlob(PutEntry put) =>
        blobs.Open(put.BlobSha).Match<Result<StoredObject>>(
            stream => new StoredObject(stream, put.Size, put.At, put.Md5, put.BlobSha, put.ContentType, put.Metadata),
            err => err);

    private Result<ObjectStat> StatFrom(Bucket b, string bucket, string key) =>
        b.Index.GetCurrentPut(key) is Result<PutEntry?>.Success { Value: { } put }
            ? new ObjectStat(put.Size, put.At, put.Md5, put.BlobSha, put.ContentType, put.Metadata)
            : new NotFoundError($"{bucket}/{key}");
}
