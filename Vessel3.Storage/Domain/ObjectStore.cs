using System.Diagnostics.CodeAnalysis;

namespace Vessel3.Storage;

internal interface IObjectStore
{
    Task<Result<PutOutcome>> Put(ObjectPutRequest request);
    Task<Result<PutOutcome>> Put(string bucket, string key, Stream body, long? declaredSize, string? contentType, string? declaredSha256, string? declaredMd5Base64, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string> tags, ChecksumSet declaredChecksums, CancellationToken ct, Retention? retention = null, bool legalHoldOn = false, IReadOnlyDictionary<string, string>? systemHeaders = null);
    Task<Result<CopyOutcome>> Copy(string destBucket, string destKey, string srcBucket, string srcKey, PreconditionRules? sourceConditions = null, IReadOnlyDictionary<string, string>? metadataOverride = null, IReadOnlyDictionary<string, string>? tagsOverride = null);
    Result<StoredObject> Get(string bucket, string key, string? versionId = null);
    Result<ObjectStat> Stat(string bucket, string key, string? versionId = null);
    Result<ObjectAttributesData> GetAttributes(string bucket, string key, string? versionId = null);
    Result<DeleteOutcome> Delete(string bucket, string key, bool bypassGovernance = false);
    Result<DeleteOutcome> DeleteVersion(string bucket, string key, string versionId, bool bypassGovernance = false);
    Result<IReadOnlyList<Result<DeleteOutcome>>> DeleteBatch(string bucket, IReadOnlyList<BatchDeleteItem> items);
    Result<IReadOnlyDictionary<string, string>> GetTagging(string bucket, string key, string? versionId);
    Result<PutTaggingOutcome> PutTagging(string bucket, string key, string? versionId, IReadOnlyDictionary<string, string> tags);
    Result<PutTaggingOutcome> DeleteTagging(string bucket, string key, string? versionId);
}

internal sealed class ObjectStore(IBucketRegistry registry, IBlobPool blobs, IPreconditionEvaluator pre, IGcGate gate) : IObjectStore
{
    public Task<Result<PutOutcome>> Put(string bucket, string key, Stream body, long? declaredSize, string? contentType, string? declaredSha256, string? declaredMd5Base64, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string> tags, ChecksumSet declaredChecksums, CancellationToken ct, Retention? retention = null, bool legalHoldOn = false, IReadOnlyDictionary<string, string>? systemHeaders = null) =>
        Put(new ObjectPutRequest(bucket, key, body, declaredSize, contentType, declaredSha256, declaredMd5Base64, metadata, tags, declaredChecksums, ct, retention, legalHoldOn, systemHeaders));

    public async Task<Result<PutOutcome>> Put(ObjectPutRequest req)
    {
        using var lease = await gate.Writing();
        var written = await blobs.Write(req.Body, req.DeclaredSize, req.DeclaredChecksums.ToIntent(), req.Ct);
        return !written.TryGetValue(out var blob, out var blobErr)
            ? blobErr
            : ValidateDigests(blob, req.DeclaredSha256, req.DeclaredMd5Base64) is { } digestErr
                ? digestErr
                : !ChecksumValidator.Validate(blob, req.DeclaredChecksums, req.Body, out var toStore, out var checksumErr)
                    ? checksumErr
                    : RecordPut(req.Bucket, req.Key, blob, req.ContentType, req.Metadata, req.Tags, toStore, req.Retention, req.LegalHoldOn, req.SystemHeaders);
    }

    private static Error? ValidateDigests(StoredBlob blob, string? declaredSha256, string? declaredMd5Base64) =>
        declaredSha256 is not null && !string.Equals(blob.Sha, declaredSha256, StringComparison.OrdinalIgnoreCase)
            ? new BadDigestError($"sha256 declared {declaredSha256}, actual {blob.Sha}")
            : declaredMd5Base64 is not null && !string.Equals(declaredMd5Base64, Convert.ToBase64String(Convert.FromHexString(blob.Md5)), StringComparison.Ordinal)
                ? new BadDigestError($"md5 declared {declaredMd5Base64}, actual {Convert.ToBase64String(Convert.FromHexString(blob.Md5))}")
                : null;

    public Result<IReadOnlyDictionary<string, string>> GetTagging(string bucket, string key, string? versionId) =>
        IsDeleteMarkerTarget(bucket, key, versionId)
            ? new MethodNotAllowedError($"{bucket}/{key} target is a delete marker")
            : !Lookup(bucket, key, versionId).TryGetValue(out var put, out var err)
                ? err
                : put is null
                    ? new NoSuchKeyError(key)
                    : new Result<IReadOnlyDictionary<string, string>>.Success(put.Tags ?? new Dictionary<string, string>());

    public Result<PutTaggingOutcome> PutTagging(string bucket, string key, string? versionId, IReadOnlyDictionary<string, string> tags) =>
        IsDeleteMarkerTarget(bucket, key, versionId)
            ? new MethodNotAllowedError($"{bucket}/{key} target is a delete marker")
            : registry.PutTagging(bucket, key, versionId, tags);

    private bool IsDeleteMarkerTarget(string bucket, string key, string? versionId) =>
        versionId is null
            ? registry.GetCurrentKind(bucket, key) == Storage.VersionKind.DeleteMarker
            : registry.GetVersionKind(bucket, key, versionId) == Storage.VersionKind.DeleteMarker;

    public Result<PutTaggingOutcome> DeleteTagging(string bucket, string key, string? versionId) =>
        PutTagging(bucket, key, versionId, new Dictionary<string, string>());

    public Result<StoredObject> Get(string bucket, string key, string? versionId = null) =>
        !Lookup(bucket, key, versionId).TryGetValue(out var put, out var err)
            ? err
            : put is null
                ? new NoSuchKeyError(key)
                : OpenBlob(put);

    public Result<ObjectAttributesData> GetAttributes(string bucket, string key, string? versionId = null) =>
        !Lookup(bucket, key, versionId).TryGetValue(out var put, out var err)
            ? err
            : put is null
                ? new NoSuchKeyError(key)
                : new ObjectAttributesData(put.Size, put.At, put.WireEtag, put.WireSha256, put.Parts);

    public Result<ObjectStat> Stat(string bucket, string key, string? versionId = null) =>
        !Lookup(bucket, key, versionId).TryGetValue(out var put, out var err)
            ? err
            : put is null
                ? new NoSuchKeyError(key)
                : new ObjectStat(put.Size, put.At, put.WireEtag, put.WireSha256, put.ContentType, put.Metadata,
                    new ChecksumSet(put.Crc32, put.Crc32C, put.Sha1, null), put.SystemHeaders);

    private Result<PutEntry?> Lookup(string bucket, string key, string? versionId) =>
        versionId is null
            ? registry.GetCurrentPut(bucket, key)
            : registry.GetVersion(bucket, key, versionId);

    public Result<DeleteOutcome> Delete(string bucket, string key, bool bypassGovernance = false) =>
        registry.AppendDelete(bucket, key, bypassGovernance);

    public Result<DeleteOutcome> DeleteVersion(string bucket, string key, string versionId, bool bypassGovernance = false) =>
        registry.HardDeleteVersion(bucket, key, versionId, bypassGovernance);

    public Result<IReadOnlyList<Result<DeleteOutcome>>> DeleteBatch(string bucket, IReadOnlyList<BatchDeleteItem> items) =>
        registry.DeleteBatch(bucket, items);

    public async Task<Result<CopyOutcome>> Copy(string destBucket, string destKey, string srcBucket, string srcKey, PreconditionRules? sourceConditions = null, IReadOnlyDictionary<string, string>? metadataOverride = null, IReadOnlyDictionary<string, string>? tagsOverride = null)
    {
        using var lease = await gate.Writing();
        return CopyUnderGate(destBucket, destKey, srcBucket, srcKey, sourceConditions, metadataOverride, tagsOverride);
    }

    private Result<CopyOutcome> CopyUnderGate(string destBucket, string destKey, string srcBucket, string srcKey, PreconditionRules? sourceConditions, IReadOnlyDictionary<string, string>? metadataOverride, IReadOnlyDictionary<string, string>? tagsOverride)
    {
        if (!registry.GetCurrentPut(srcBucket, srcKey).TryGetValue(out var srcEntry, out var err))
            return err;

        if (srcEntry is null)
            return new NoSuchKeyError(srcKey);

        if (sourceConditions is { } cond && pre.Evaluate(cond, srcEntry.Md5, srcEntry.At) is Precondition.Failed)
            return new PreconditionFailedError($"{srcBucket}/{srcKey}");

        var putReq = new PutRequest(
            BlobSha: srcEntry.BlobSha,
            Md5: srcEntry.Md5,
            Size: srcEntry.Size,
            ContentType: srcEntry.ContentType,
            Metadata: metadataOverride ?? srcEntry.Metadata,
            Parts: srcEntry.Parts,
            Tags: tagsOverride ?? srcEntry.Tags,
            SystemHeaders: srcEntry.SystemHeaders);

        return !registry.AppendPut(destBucket, destKey, putReq).TryGetValue(out var written, out var putErr)
            ? putErr
            : new CopyOutcome(written.WireEtag, written.At, written.VersionId);
    }

    private Result<PutOutcome> RecordPut(string bucket, string key, StoredBlob blob, string? contentType, IReadOnlyDictionary<string, string> metadata, IReadOnlyDictionary<string, string> tags, ChecksumSet toStore, Retention? retention, bool legalHoldOn, IReadOnlyDictionary<string, string>? systemHeaders)
    {
        var resolved = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType;
        var putReq = new PutRequest(
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
            LegalHoldOn: legalHoldOn,
            SystemHeaders: systemHeaders);

        return !registry.AppendPut(bucket, key, putReq).TryGetValue(out var entry, out var err)
            ? err
            : new PutOutcome(blob.Md5, blob.Sha, entry.VersionId, blob.Size, toStore);
    }

    private Result<StoredObject> OpenBlob(PutEntry put)
    {
        var sums = new ChecksumSet(put.Crc32, put.Crc32C, put.Sha1, null);
        return put.Parts is { } parts
            ? new StoredObject(new ConcatStream(parts, blobs), put.Size, put.At, put.WireEtag, "", put.ContentType, put.Metadata, sums, put.SystemHeaders)
            : !blobs.Open(put.BlobSha).TryGetValue(out var stream, out var err)
                ? err
                : new StoredObject(stream, put.Size, put.At, put.Md5, put.BlobSha, put.ContentType, put.Metadata, sums, put.SystemHeaders);
    }
}
