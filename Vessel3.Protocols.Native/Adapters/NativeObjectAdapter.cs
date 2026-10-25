using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Vessel3.Primitives;
using Vessel3.Protocols.Native.Headers;
using Vessel3.Protocols.Native.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native.Adapters;

internal sealed class NativeObjectAdapter(
    IObjectStore objects,
    IBucketRegistry registry,
    IChunkStager stager,
    IBucketLister lister)
{
    public IResult ListObjects(string bucket, HttpContext context)
    {
        RequestTrace.SetTarget("ListObjects", bucket);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var prefix = NativeHeaderCodec.Nullify(context.Request.Query["prefix"]);
        var marker = NativeHeaderCodec.Nullify(context.Request.Query["marker"]);
        var delimiter = NativeHeaderCodec.Nullify(context.Request.Query["delimiter"]);
        var limit = int.TryParse(context.Request.Query["limit"], out var parsedLimit) && parsedLimit > 0
            ? Math.Clamp(parsedLimit, 1, 1000)
            : 1000;

        var result = lister.List(new ListRequest(bucket, prefix, delimiter, marker, limit), continuationToken: null);
        if (!result.TryGetValue(out var page, out var error))
        {
            return error.ToHttpResult();
        }

        List<ObjectSummaryDto> objectsList = new(page.Entries.Count);
        List<string> prefixes = [];
        foreach (var entry in page.Entries)
        {
            if (entry is ListEntry.Contents contents)
                objectsList.Add(new ObjectSummaryDto(contents.Key, contents.Size, contents.Etag, contents.LastModified, null));
            else if (entry is ListEntry.CommonPrefix prefixEntry)
                prefixes.Add(prefixEntry.Key);
        }
        var nextMarker = page.NextContinuationToken ?? page.LastKey;

        var dto = new ObjectsPageDto(objectsList, prefixes, page.IsTruncated, nextMarker);
        return Results.Json(dto, NativeJsonContext.Default.ObjectsPageDto);
    }

    public async Task<IResult> GetObject(string bucket, string key, HttpContext context)
    {
        RequestTrace.SetTarget("GetObject", bucket, key);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var versionId = NativeHeaderCodec.Nullify(context.Request.Query["versionId"]);
        var result = await objects.Get(bucket, key, versionId, context.RequestAborted);
        if (!result.TryGetValue(out var storedObject, out var error))
        {
            return error.ToHttpResult();
        }

        NativeHeaderCodec.ApplyObjectHeaders(context.Response.Headers, storedObject, versionId);

        RequestTrace.Current?.MarkHandled();
        return Results.Stream(
            storedObject.Body,
            contentType: storedObject.ContentType ?? "application/octet-stream",
            lastModified: storedObject.LastModified,
            entityTag: new EntityTagHeaderValue($"\"{storedObject.Etag}\""),
            enableRangeProcessing: true);
    }

    public IResult HeadObject(string bucket, string key, HttpContext context)
    {
        RequestTrace.SetTarget("HeadObject", bucket, key);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Read) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var versionId = NativeHeaderCodec.Nullify(context.Request.Query["versionId"]);
        var result = objects.Stat(bucket, key, versionId);
        if (!result.TryGetValue(out var stat, out var error))
        {
            return error.ToHttpResult();
        }

        NativeHeaderCodec.ApplyObjectHeaders(context.Response.Headers, stat, versionId);
        return Results.Ok();
    }

    public async Task<IResult> PutObject(string bucket, string key, HttpContext context)
    {
        RequestTrace.SetTarget("PutObject", bucket, key);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Write) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var metadata = NativeHeaderCodec.ExtractMetadata(context.Request.Headers);

        var putRequest = new ObjectPutRequest(
            bucket,
            key,
            context.Request.Body,
            context.Request.ContentLength,
            context.Request.ContentType,
            DeclaredSha256: null,
            DeclaredMd5Base64: null,
            Metadata: metadata,
            Tags: FrozenDictionary<string, string>.Empty,
            DeclaredChecksums: ChecksumSet.Empty,
            Ct: context.RequestAborted,
            Protocol: "native",
            Actor: caller?.Username,
            Host: context.Request.Host.Value);

        var result = await objects.Put(putRequest);
        if (!result.TryGetValue(out var outcome, out var err))
        {
            return err.ToHttpResult();
        }

        return Results.Json(new PutObjectResultDto(outcome.Etag, outcome.VersionId, outcome.Size, outcome.Sha256), NativeJsonContext.Default.PutObjectResultDto);
    }

    public IResult DeleteObject(string bucket, string key, HttpContext context)
    {
        RequestTrace.SetTarget("DeleteObject", bucket, key);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Write) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var versionId = NativeHeaderCodec.Nullify(context.Request.Query["versionId"]);
        var result = versionId is not null
            ? objects.DeleteVersion(bucket, key, versionId)
            : objects.Delete(bucket, key);

        if (!result.TryGetValue(out _, out var err))
        {
            return err.ToHttpResult();
        }

        return Results.NoContent();
    }

    public async Task<IResult> PostUpload(string bucket, string key, HttpContext context)
    {
        if (context.Request.Query.TryGetValue("uploadId", out var uploadIdVal) && !string.IsNullOrEmpty(uploadIdVal))
        {
            return await CompleteUpload(bucket, key, uploadIdVal.ToString(), context);
        }

        return CreateUpload(bucket, key, context);
    }

    public async Task<IResult> UploadPart(string bucket, string key, HttpContext context)
    {
        RequestTrace.SetTarget("UploadPart", bucket, key);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Write) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        if (!context.Request.Query.TryGetValue("uploadId", out var uploadIdVal) || string.IsNullOrEmpty(uploadIdVal))
        {
            return Results.BadRequest(new ErrorDto("missing_upload_id", "uploadId query parameter is required"));
        }

        if (!context.Request.Query.TryGetValue("partNumber", out var partVal) || !int.TryParse(partVal, CultureInfo.InvariantCulture, out var partNumber) || partNumber <= 0)
        {
            return Results.BadRequest(new ErrorDto("invalid_part_number", "partNumber query parameter must be a positive integer"));
        }

        var uploadId = uploadIdVal.ToString();
        if (!TryValidateSession(uploadId, bucket, key, out var session, out var sessionError))
        {
            return sessionError;
        }

        var stageResult = await stager.StageChunk(
            uploadId,
            partNumber.ToString(CultureInfo.InvariantCulture),
            context.Request.Body,
            context.Request.ContentLength,
            DeclaredChecksums.Empty,
            context.RequestAborted);

        if (!stageResult.TryGetValue(out var chunk, out var stageErr))
        {
            return stageErr.ToHttpResult();
        }

        return Results.Json(new UploadPartDto(partNumber, chunk.Md5), NativeJsonContext.Default.UploadPartDto);
    }

    public IResult AbortUpload(string bucket, string key, HttpContext context)
    {
        RequestTrace.SetTarget("AbortUpload", bucket, key);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Write) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        if (!context.Request.Query.TryGetValue("uploadId", out var uploadIdVal) || string.IsNullOrEmpty(uploadIdVal))
        {
            return Results.BadRequest(new ErrorDto("missing_upload_id", "uploadId query parameter is required"));
        }

        var uploadId = uploadIdVal.ToString();
        if (!TryValidateSession(uploadId, bucket, key, out var session, out var sessionError))
        {
            return sessionError;
        }

        var abortResult = stager.AbortSession(uploadId);
        return abortResult.Match<IResult>(
            () => Results.NoContent(),
            err => err.ToHttpResult());
    }

    private IResult CreateUpload(string bucket, string key, HttpContext context)
    {
        RequestTrace.SetTarget("CreateUpload", bucket, key);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Write) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        var metadata = NativeHeaderCodec.ExtractMetadata(context.Request.Headers);
        var contentType = context.Request.ContentType;

        var result = stager.CreateSession(bucket, key, contentType, metadata);
        if (!result.TryGetValue(out var session, out var error))
        {
            return error.ToHttpResult();
        }

        return Results.Json(new CreateUploadResultDto(session.SessionId, bucket, key), NativeJsonContext.Default.CreateUploadResultDto);
    }

    private async Task<IResult> CompleteUpload(
        string bucket,
        string key,
        string uploadId,
        HttpContext context)
    {
        RequestTrace.SetTarget("CompleteUpload", bucket, key);
        var caller = context.GetCaller();
        if (registry.AuthorizeAccess(bucket, caller, BucketCapability.Write) is Result.Failure authFail)
        {
            return authFail.Error.ToHttpResult();
        }

        if (!TryValidateSession(uploadId, bucket, key, out var session, out var sessionError))
        {
            return sessionError;
        }

        var reqDto = await JsonSerializer.DeserializeAsync(context.Request.Body, NativeJsonContext.Default.CompleteUploadRequestDto, context.RequestAborted);
        if (reqDto.Parts is null || reqDto.Parts.Count == 0)
        {
            return Results.BadRequest(new ErrorDto("empty_parts", "At least one part is required to complete upload"));
        }

        var listResult = stager.ListChunks(uploadId);
        if (!listResult.TryGetValue(out var stagedChunks, out var listErr))
        {
            return listErr.ToHttpResult();
        }

        var stagedMap = stagedChunks.ToDictionary(c => c.Token, StringComparer.Ordinal);
        List<MultipartPart> ordered = new(reqDto.Parts.Count);
        var prevPart = 0;

        foreach (var part in reqDto.Parts.OrderBy(p => p.PartNumber))
        {
            if (part.PartNumber <= prevPart)
            {
                return Results.BadRequest(new ErrorDto("invalid_part_order", $"Duplicate or non-ascending part number: {part.PartNumber}"));
            }

            var token = part.PartNumber.ToString(CultureInfo.InvariantCulture);
            if (!stagedMap.TryGetValue(token, out var chunk))
            {
                return Results.BadRequest(new ErrorDto("missing_part", $"Part {part.PartNumber} has not been uploaded"));
            }

            ordered.Add(new MultipartPart(part.PartNumber, chunk.BlobSha, chunk.Md5, chunk.Size, chunk.Crc32, chunk.Crc32C, chunk.Sha1));
            prevPart = part.PartNumber;
        }

        var wireEtag = $"{Guid.NewGuid():N}";
        var commitResult = await stager.Commit(uploadId, ordered, wireEtag, ChecksumSet.Empty, context.RequestAborted);
        if (!commitResult.TryGetValue(out var outcome, out var commitErr))
        {
            return commitErr.ToHttpResult();
        }

        return Results.Json(new PutObjectResultDto(wireEtag, outcome.VersionId, outcome.TotalSize, Sha256: null), NativeJsonContext.Default.PutObjectResultDto);
    }

    private bool TryValidateSession(
        string uploadId,
        string bucket,
        string key,
        [NotNullWhen(true)] out StagedSession? session,
        [NotNullWhen(false)] out IResult? errorResult)
    {
        var sessionResult = stager.GetSession(uploadId);
        if (!sessionResult.TryGetValue(out session, out var sessionErr))
        {
            errorResult = sessionErr.ToHttpResult();
            return false;
        }

        if (!string.Equals(session.Bucket, bucket, StringComparison.Ordinal) || !string.Equals(session.Key, key, StringComparison.Ordinal))
        {
            session = null;
            errorResult = Results.BadRequest(new ErrorDto("upload_mismatch", "Upload session does not match bucket or key"));
            return false;
        }

        errorResult = null;
        return true;
    }
}
