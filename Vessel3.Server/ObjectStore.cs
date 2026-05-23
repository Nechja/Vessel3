using Vessel3.Server.Storage;

namespace Vessel3.Server;

internal sealed record PutOutcome(string Etag, string Sha256, string VersionId, long Size, ChecksumSet Checksums);
internal sealed record CopyOutcome(string Etag, DateTimeOffset LastModified, string VersionId);
internal sealed record StoredObject(Stream Body, long Size, DateTimeOffset LastModified, string Etag, string Sha256, string ContentType, IReadOnlyDictionary<string, string> Metadata, ChecksumSet Checksums);
internal sealed record ObjectStat(long Size, DateTimeOffset LastModified, string Etag, string Sha256, string ContentType, IReadOnlyDictionary<string, string> Metadata, ChecksumSet Checksums);

internal sealed record ObjectAttributesData(
    long Size, DateTimeOffset LastModified, string Etag, string Sha256,
    IReadOnlyList<Vessel3.Server.Storage.MultipartPart>? Parts);

internal interface IObjectStore
{
    Task<Result<PutOutcome>> Put(string bucket, string key, Stream body, long? declaredSize, string? contentType, string? declaredSha256, string? declaredMd5Base64, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string> tags, ChecksumSet declaredChecksums, CancellationToken ct, Retention? retention = null, bool legalHoldOn = false);
    Result<CopyOutcome> Copy(string destBucket, string destKey, string srcBucket, string srcKey, IHeaderDictionary copyHeaders, IReadOnlyDictionary<string, string>? metadataOverride, IReadOnlyDictionary<string, string>? tagsOverride);
    Result<StoredObject> Get(string bucket, string key, string? versionId = null);
    Result<ObjectStat> Stat(string bucket, string key, string? versionId = null);
    Result<ObjectAttributesData> GetAttributes(string bucket, string key, string? versionId = null);
    Result<DeleteOutcome> Delete(string bucket, string key, bool bypassGovernance = false);
    Result<DeleteOutcome> DeleteVersion(string bucket, string key, string versionId, bool bypassGovernance = false);
    Result<IReadOnlyDictionary<string, string>> GetTagging(string bucket, string key, string? versionId);
    Result<PutTaggingOutcome> PutTagging(string bucket, string key, string? versionId, IReadOnlyDictionary<string, string> tags);
    Result<PutTaggingOutcome> DeleteTagging(string bucket, string key, string? versionId);
}

internal sealed class ObjectStore(IBucketRegistry registry, IBlobPool blobs, IPreconditionEvaluator pre) : IObjectStore
{
    public async Task<Result<PutOutcome>> Put(string bucket, string key, Stream body, long? declaredSize, string? contentType, string? declaredSha256, string? declaredMd5Base64, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string> tags, ChecksumSet declaredChecksums, CancellationToken ct, Retention? retention = null, bool legalHoldOn = false)
    {
        var written = await blobs.Write(body, declaredSize, ct);
        if (written is Result<StoredBlob>.Failure bf) return bf.Error;
        var blob = ((Result<StoredBlob>.Success)written).Value;

        if (declaredSha256 is not null && !string.Equals(blob.Sha, declaredSha256, StringComparison.OrdinalIgnoreCase))
            return new BadDigestError($"sha256 declared {declaredSha256}, actual {blob.Sha}");
        if (declaredMd5Base64 is not null
            && !string.Equals(declaredMd5Base64, Convert.ToBase64String(Convert.FromHexString(blob.Md5)), StringComparison.Ordinal))
            return new BadDigestError($"md5 declared {declaredMd5Base64}, actual {Convert.ToBase64String(Convert.FromHexString(blob.Md5))}");

        if (declaredChecksums.Crc32 is { } c32 && !string.Equals(c32, blob.Crc32, StringComparison.OrdinalIgnoreCase))
            return new BadDigestError($"crc32 declared (hex){c32}, actual {blob.Crc32}");
        if (declaredChecksums.Crc32C is { } c32c && !string.Equals(c32c, blob.Crc32C, StringComparison.OrdinalIgnoreCase))
            return new BadDigestError($"crc32c declared (hex){c32c}, actual {blob.Crc32C}");
        if (declaredChecksums.Sha1 is { } s1 && !string.Equals(s1, blob.Sha1, StringComparison.OrdinalIgnoreCase))
            return new BadDigestError($"sha1 declared (hex){s1}, actual {blob.Sha1}");
        if (declaredChecksums.Sha256 is { } s256 && !string.Equals(s256, blob.Sha, StringComparison.OrdinalIgnoreCase))
            return new BadDigestError($"sha256(checksum) declared (hex){s256}, actual {blob.Sha}");

        var toStore = new ChecksumSet(
            declaredChecksums.Crc32 is null ? null : blob.Crc32,
            declaredChecksums.Crc32C is null ? null : blob.Crc32C,
            declaredChecksums.Sha1 is null ? null : blob.Sha1,
            declaredChecksums.Sha256 is null ? null : blob.Sha);
        return RecordPut(bucket, key, blob, contentType, metadata, tags, toStore, retention, legalHoldOn);
    }

    public Result<IReadOnlyDictionary<string, string>> GetTagging(string bucket, string key, string? versionId) =>
        IsDeleteMarkerTarget(bucket, key, versionId)
            ? new MethodNotAllowedError($"{bucket}/{key} target is a delete marker")
            : Lookup(bucket, key, versionId).Match<Result<IReadOnlyDictionary<string, string>>>(
                put => put is null
                    ? new NotFoundError($"{bucket}/{key}")
                    : new Result<IReadOnlyDictionary<string, string>>.Success(put.Tags ?? new Dictionary<string, string>()),
                err => err);

    public Result<PutTaggingOutcome> PutTagging(string bucket, string key, string? versionId, IReadOnlyDictionary<string, string> tags) =>
        IsDeleteMarkerTarget(bucket, key, versionId)
            ? new MethodNotAllowedError($"{bucket}/{key} target is a delete marker")
            : registry.PutTagging(bucket, key, versionId, tags);

    private bool IsDeleteMarkerTarget(string bucket, string key, string? versionId) =>
        versionId is null
            ? registry.GetCurrentKind(bucket, key) == Storage.BucketIndex.KindDeleteMarker
            : registry.GetVersionKind(bucket, key, versionId) == Storage.BucketIndex.KindDeleteMarker;

    public Result<PutTaggingOutcome> DeleteTagging(string bucket, string key, string? versionId) =>
        PutTagging(bucket, key, versionId, new Dictionary<string, string>());

    public Result<StoredObject> Get(string bucket, string key, string? versionId = null) =>
        Lookup(bucket, key, versionId).Match<Result<StoredObject>>(
            put => put is null
                ? new NotFoundError($"{bucket}/{key}")
                : OpenBlob(put),
            err => err);

    public Result<ObjectAttributesData> GetAttributes(string bucket, string key, string? versionId = null) =>
        Lookup(bucket, key, versionId).Match<Result<ObjectAttributesData>>(
            put => put is null
                ? new NotFoundError($"{bucket}/{key}")
                : new ObjectAttributesData(put.Size, put.At, put.WireEtag, put.WireSha256, put.Parts),
            err => err);

    public Result<ObjectStat> Stat(string bucket, string key, string? versionId = null) =>
        Lookup(bucket, key, versionId).Match<Result<ObjectStat>>(
            put => put is null
                ? new NotFoundError($"{bucket}/{key}")
                : new ObjectStat(put.Size, put.At, put.WireEtag, put.WireSha256, put.ContentType, put.Metadata,
                    new ChecksumSet(put.Crc32, put.Crc32C, put.Sha1, null)),
            err => err);

    private Result<PutEntry?> Lookup(string bucket, string key, string? versionId) =>
        versionId is null
            ? registry.GetCurrentPut(bucket, key)
            : registry.GetVersion(bucket, key, versionId);

    public Result<DeleteOutcome> Delete(string bucket, string key, bool bypassGovernance = false) =>
        registry.AppendDelete(bucket, key, bypassGovernance);

    public Result<DeleteOutcome> DeleteVersion(string bucket, string key, string versionId, bool bypassGovernance = false) =>
        registry.HardDeleteVersion(bucket, key, versionId, bypassGovernance);

    public Result<CopyOutcome> Copy(string destBucket, string destKey, string srcBucket, string srcKey, IHeaderDictionary copyHeaders, IReadOnlyDictionary<string, string>? metadataOverride, IReadOnlyDictionary<string, string>? tagsOverride) =>
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
                            Parts: srcEntry.Parts,
                            Tags: tagsOverride ?? srcEntry.Tags))
                        .Match<Result<CopyOutcome>>(
                            written => new CopyOutcome(written.WireEtag, written.At, written.VersionId),
                            err => err),
            err => err);

    private Result<PutOutcome> RecordPut(string bucket, string key, StoredBlob blob, string? contentType, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string> tags, ChecksumSet toStore, Retention? retention, bool legalHoldOn)
    {
        var resolved = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType;
        return registry.AppendPut(bucket, key, new PutRequest(
                BlobSha: blob.Sha,
                Md5: blob.Md5,
                Size: blob.Size,
                ContentType: resolved,
                Metadata: metadata,
                Tags: tags,
                Crc32: toStore.Crc32,
                Crc32C: toStore.Crc32C,
                Sha1: toStore.Sha1,
                Retention: retention,
                LegalHoldOn: legalHoldOn)).Match<Result<PutOutcome>>(
            entry => new PutOutcome(blob.Md5, blob.Sha, entry.VersionId, blob.Size, toStore),
            err => err);
    }

    private Result<StoredObject> OpenBlob(PutEntry put)
    {
        var sums = new ChecksumSet(put.Crc32, put.Crc32C, put.Sha1, null);
        return put.Parts is { } parts
            ? new StoredObject(new ConcatStream(parts, blobs), put.Size, put.At, put.WireEtag, "", put.ContentType, put.Metadata, sums)
            : blobs.Open(put.BlobSha).Match<Result<StoredObject>>(
                stream => new StoredObject(stream, put.Size, put.At, put.Md5, put.BlobSha, put.ContentType, put.Metadata, sums),
                err => err);
    }
}
