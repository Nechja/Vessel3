using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record PutOutcome(string Etag, string VersionId, long Size);
internal sealed record StoredObject(FileStream Body, long Size, DateTimeOffset LastModified, string Etag, string ContentType);
internal sealed record ObjectStat(long Size, DateTimeOffset LastModified, string Etag, string ContentType);

internal sealed class ObjectStore(BucketRegistry registry, BlobPool blobs)
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
        var entry = b.AppendPut(key, blob.Sha, blob.Size, resolved);
        return new PutOutcome(blob.Sha, entry.VersionId, blob.Size);
    }

    private Result<StoredObject> GetFrom(Bucket b, string bucket, string key) =>
        b.Index.GetCurrent(key) is Result<VersionEntry?>.Success { Value: { } entry }
        && entry.Kind is not EventKind.DeleteMarker
            ? OpenBlob(entry)
            : new NotFoundError($"{bucket}/{key}");

    private Result<StoredObject> OpenBlob(VersionEntry entry) =>
        blobs.Open(entry.BlobSha) switch
        {
            Result<FileStream>.Success ok =>
                new StoredObject(ok.Value, entry.Size, entry.At, entry.BlobSha, entry.ContentType),
            Result<FileStream>.Failure of => of.Error,
            _ => throw new System.Diagnostics.UnreachableException(),
        };

    private Result<ObjectStat> StatFrom(Bucket b, string bucket, string key) =>
        b.Index.GetCurrent(key) is Result<VersionEntry?>.Success { Value: { } entry }
        && entry.Kind is not EventKind.DeleteMarker
            ? new ObjectStat(entry.Size, entry.At, entry.BlobSha, entry.ContentType)
            : new NotFoundError($"{bucket}/{key}");
}
