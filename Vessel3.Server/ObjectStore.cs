using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record PutOutcome(string Etag, string Sha256, string VersionId, long Size);
internal sealed record StoredObject(Stream Body, long Size, DateTimeOffset LastModified, string Etag, string Sha256, string ContentType);
internal sealed record ObjectStat(long Size, DateTimeOffset LastModified, string Etag, string Sha256, string ContentType);

internal interface IObjectStore
{
    Task<Result<PutOutcome>> Put(string bucket, string key, Stream body, long? declaredSize, string? contentType, CancellationToken ct);
    Result<StoredObject> Get(string bucket, string key);
    Result<ObjectStat> Stat(string bucket, string key);
    Result<bool> Delete(string bucket, string key);
}

internal sealed class ObjectStore(IBucketRegistry registry, IBlobPool blobs) : IObjectStore
{
    public Task<Result<PutOutcome>> Put(string bucket, string key, Stream body, long? declaredSize, string? contentType, CancellationToken ct) =>
        !registry.IsValidName(bucket) ? Task.FromResult<Result<PutOutcome>>(new InvalidPathError(bucket))
        : string.IsNullOrEmpty(key) ? Task.FromResult<Result<PutOutcome>>(new InvalidPathError($"{bucket}/{key}"))
        : registry.Open(bucket) is { } b
            ? PutInto(b, key, body, declaredSize, contentType, ct)
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

    public Result<bool> Delete(string bucket, string key) =>
        !registry.IsValidName(bucket) ? new InvalidPathError(bucket)
        : string.IsNullOrEmpty(key) ? new InvalidPathError($"{bucket}/{key}")
        : registry.Open(bucket)?.AppendHardDeleteCurrent(key) ?? false;

    private async Task<Result<PutOutcome>> PutInto(Bucket b, string key, Stream body, long? declaredSize, string? contentType, CancellationToken ct) =>
        await blobs.Write(body, declaredSize, ct) switch
        {
            Result<StoredBlob>.Success ok => RecordPut(b, key, ok.Value, contentType),
            Result<StoredBlob>.Failure bf => bf.Error,
            _ => throw new System.Diagnostics.UnreachableException(),
        };

    private PutOutcome RecordPut(Bucket b, string key, StoredBlob blob, string? contentType)
    {
        var resolved = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType;
        var entry = b.AppendPut(key, blob.Sha, blob.Md5, blob.Size, resolved);
        return new PutOutcome(blob.Md5, blob.Sha, entry.VersionId, blob.Size);
    }

    private Result<StoredObject> GetFrom(Bucket b, string bucket, string key) =>
        b.Index.GetCurrentPut(key) is Result<PutEntry?>.Success { Value: { } put }
            ? OpenBlob(put)
            : new NotFoundError($"{bucket}/{key}");

    private Result<StoredObject> OpenBlob(PutEntry put) =>
        blobs.Open(put.BlobSha).Match<Result<StoredObject>>(
            stream => new StoredObject(stream, put.Size, put.At, put.Md5, put.BlobSha, put.ContentType),
            err => err);

    private Result<ObjectStat> StatFrom(Bucket b, string bucket, string key) =>
        b.Index.GetCurrentPut(key) is Result<PutEntry?>.Success { Value: { } put }
            ? new ObjectStat(put.Size, put.At, put.Md5, put.BlobSha, put.ContentType)
            : new NotFoundError($"{bucket}/{key}");
}
