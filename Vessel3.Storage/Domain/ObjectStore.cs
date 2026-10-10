using System.Collections.Frozen;

namespace Vessel3.Storage;

internal interface IObjectStore
{
    Task<Result<PutOutcome>> Put(ObjectPutRequest request);
    Task<Result<CopyOutcome>> Copy(string destBucket, string destKey, string srcBucket, string srcKey, PreconditionRules? sourceConditions = null, IReadOnlyDictionary<string, string>? metadataOverride = null, IReadOnlyDictionary<string, string>? tagsOverride = null);
    Task<Result<StoredObject>> Get(string bucket, string key, string? versionId = null, CancellationToken ct = default);
    Result<ObjectStat> Stat(string bucket, string key, string? versionId = null);
    Result<ObjectAttributesData> GetAttributes(string bucket, string key, string? versionId = null);
    Result<DeleteOutcome> Delete(string bucket, string key, bool bypassGovernance = false);
    Result<DeleteOutcome> DeleteVersion(string bucket, string key, string versionId, bool bypassGovernance = false);
    Result<IReadOnlyList<Result<DeleteOutcome>>> DeleteBatch(string bucket, IReadOnlyList<BatchDeleteItem> items);
    Result<IReadOnlyDictionary<string, string>> GetTagging(string bucket, string key, string? versionId);
    Result<PutTaggingOutcome> PutTagging(string bucket, string key, string? versionId, IReadOnlyDictionary<string, string> tags);
    Result<PutTaggingOutcome> DeleteTagging(string bucket, string key, string? versionId);
}

internal sealed partial class ObjectStore(IBucketRegistry registry, IBlobPool blobs, IPreconditionEvaluator pre, IGcGate gate, IWebhookEventPublisher? publisher = null, ILogger<ObjectStore>? logger = null) : IObjectStore
{
    public async Task<Result<PutOutcome>> Put(ObjectPutRequest req)
    {
        using var lease = await gate.Writing();
        var written = await blobs.Write(req.Body, req.DeclaredSize, req.Checksums.ToIntent(), req.Ct);
        if (!written.TryGetValue(out var blob, out var blobErr))
            return blobErr;

        if (ValidateDigests(blob, req.DeclaredSha256, req.DeclaredMd5Base64) is { } digestErr)
        {
            if (logger is not null)
            {
                LogDigestMismatch(logger, req.Bucket, req.Key, digestErr.Message);
            }
            return digestErr;
        }

        return !ChecksumValidator.Validate(blob, req.Checksums, req.Body, out var toStore, out var checksumErr)
            ? checksumErr
            : RecordPut(req, blob, toStore);
    }

    private static Error? ValidateDigests(StoredBlob blob, string? declaredSha256, string? declaredMd5Base64)
    {
        if (declaredSha256 is not null && !string.Equals(blob.Sha, declaredSha256, StringComparison.OrdinalIgnoreCase))
            return new BadDigestError($"sha256 declared {declaredSha256}, actual {blob.Sha}");

        var actualMd5 = Convert.ToBase64String(Convert.FromHexString(blob.Md5));
        return declaredMd5Base64 is not null && !string.Equals(declaredMd5Base64, actualMd5, StringComparison.Ordinal)
            ? new BadDigestError($"md5 declared {declaredMd5Base64}, actual {actualMd5}")
            : null;
    }

    public Result<IReadOnlyDictionary<string, string>> GetTagging(string bucket, string key, string? versionId)
    {
        if (IsDeleteMarkerTarget(bucket, key, versionId))
            return new MethodNotAllowedError($"{bucket}/{key} target is a delete marker");

        if (!Lookup(bucket, key, versionId).TryGetValue(out var put, out var err))
            return err;

        return put is null
            ? new NoSuchKeyError(key)
            : new Result<IReadOnlyDictionary<string, string>>.Success(put.Tags ?? FrozenDictionary<string, string>.Empty);
    }

    public Result<PutTaggingOutcome> PutTagging(string bucket, string key, string? versionId, IReadOnlyDictionary<string, string> tags) =>
        IsDeleteMarkerTarget(bucket, key, versionId)
            ? new MethodNotAllowedError($"{bucket}/{key} target is a delete marker")
            : registry.PutTagging(bucket, key, versionId, tags);

    private bool IsDeleteMarkerTarget(string bucket, string key, string? versionId) =>
        versionId is null
            ? registry.GetCurrentKind(bucket, key) == Storage.VersionKind.DeleteMarker
            : registry.GetVersionKind(bucket, key, versionId) == Storage.VersionKind.DeleteMarker;

    public Result<PutTaggingOutcome> DeleteTagging(string bucket, string key, string? versionId) =>
        PutTagging(bucket, key, versionId, FrozenDictionary<string, string>.Empty);

    public async Task<Result<StoredObject>> Get(string bucket, string key, string? versionId = null, CancellationToken ct = default)
    {
        if (!Lookup(bucket, key, versionId).TryGetValue(out var put, out var err))
            return err;

        return put is null
            ? new NoSuchKeyError(key)
            : await OpenBlob(put, ct);
    }

    public Result<ObjectAttributesData> GetAttributes(string bucket, string key, string? versionId = null)
    {
        if (!Lookup(bucket, key, versionId).TryGetValue(out var put, out var err))
            return err;

        return put is null
            ? new NoSuchKeyError(key)
            : new ObjectAttributesData(put.Size, put.At, put.WireEtag, put.WireSha256, put.Parts);
    }

    public Result<ObjectStat> Stat(string bucket, string key, string? versionId = null)
    {
        if (!Lookup(bucket, key, versionId).TryGetValue(out var put, out var err))
            return err;

        return put is null
            ? new NoSuchKeyError(key)
            : new ObjectStat(put.Size, put.At, put.WireEtag, put.WireSha256, put.ContentType, put.Metadata,
                new ChecksumSet(put.Crc32, put.Crc32C, put.Sha1, null), put.SystemHeaders, put.VersionId);
    }

    private Result<PutEntry?> Lookup(string bucket, string key, string? versionId) =>
        versionId is null
            ? registry.GetCurrentPut(bucket, key)
            : registry.GetVersion(bucket, key, versionId);

    public Result<DeleteOutcome> Delete(string bucket, string key, bool bypassGovernance = false)
    {
        var result = registry.AppendDelete(bucket, key, bypassGovernance);
        if (result.TryGetValue(out var outcome, out _))
            publisher?.Publish(VesselEvents.ObjectDeleted(bucket, key, outcome.VersionId, outcome.IsDeleteMarker));
        return result;
    }

    public Result<DeleteOutcome> DeleteVersion(string bucket, string key, string versionId, bool bypassGovernance = false)
    {
        var result = registry.HardDeleteVersion(bucket, key, versionId, bypassGovernance);
        if (result.TryGetValue(out var outcome, out _))
            publisher?.Publish(VesselEvents.ObjectDeleted(bucket, key, outcome.VersionId, outcome.IsDeleteMarker));
        return result;
    }

    public Result<IReadOnlyList<Result<DeleteOutcome>>> DeleteBatch(string bucket, IReadOnlyList<BatchDeleteItem> items)
    {
        var result = registry.DeleteBatch(bucket, items);
        if (!result.TryGetValue(out var outcomes, out _))
            return result;

        for (var i = 0; i < items.Count && i < outcomes.Count; i++)
        {
            if (outcomes[i].TryGetValue(out var outcome, out _))
                publisher?.Publish(VesselEvents.ObjectDeleted(bucket, items[i].Key, outcome.VersionId, outcome.IsDeleteMarker));
        }

        return result;
    }

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

        if (!registry.AppendPut(destBucket, destKey, putReq).TryGetValue(out var written, out var putErr))
            return putErr;

        publisher?.Publish(VesselEvents.ObjectCreated(
            destBucket, destKey, srcEntry.Size, written.WireEtag, written.VersionId, srcEntry.BlobSha, srcEntry.ContentType));

        return new CopyOutcome(written.WireEtag, written.At, written.VersionId);
    }

    private Result<PutOutcome> RecordPut(ObjectPutRequest req, StoredBlob blob, ChecksumSet toStore)
    {
        var resolved = string.IsNullOrEmpty(req.ContentType) ? "application/octet-stream" : req.ContentType;
        var putReq = new PutRequest(
            BlobSha: blob.Sha,
            Md5: blob.Md5,
            Size: blob.Size,
            ContentType: resolved,
            Metadata: req.Metadata ?? FrozenDictionary<string, string>.Empty,
            Tags: req.Tags,
            Crc32: toStore.Crc32,
            Crc32C: toStore.Crc32C,
            Sha1: toStore.Sha1,
            Retention: req.Retention,
            LegalHoldOn: req.LegalHoldOn,
            SystemHeaders: req.SystemHeaders);

        if (!registry.AppendPut(req.Bucket, req.Key, putReq).TryGetValue(out var entry, out var err))
            return err;

        publisher?.Publish(VesselEvents.ObjectCreated(
            req.Bucket, req.Key, blob.Size, entry.WireEtag, entry.VersionId, blob.Sha, resolved,
            protocol: req.Protocol, actor: req.Actor, host: req.Host));

        return new PutOutcome(blob.Md5, blob.Sha, entry.VersionId, blob.Size, toStore);
    }

    private async Task<Result<StoredObject>> OpenBlob(PutEntry put, CancellationToken ct)
    {
        var sums = new ChecksumSet(put.Crc32, put.Crc32C, put.Sha1, null);
        if (put.Parts is { } parts)
            return new StoredObject(new ConcatStream(parts, blobs), put.Size, put.At, put.WireEtag, "", put.ContentType, put.Metadata, sums, put.SystemHeaders, put.VersionId);

        var openResult = await blobs.Open(put.BlobSha, ct);
        return !openResult.TryGetValue(out var stream, out var err)
            ? err
            : new StoredObject(stream, put.Size, put.At, put.Md5, put.BlobSha, put.ContentType, put.Metadata, sums, put.SystemHeaders, put.VersionId);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Digest mismatch on {Bucket}/{Key}: {Reason}")]
    private static partial void LogDigestMismatch(ILogger logger, string bucket, string key, string reason);
}
