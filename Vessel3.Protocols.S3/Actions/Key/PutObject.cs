using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace Vessel3.Server.S3.Key;

internal sealed class PutObject(IObjectStore objects, IBucketRegistry registry, IHttpResultMapper http, IPreconditionEvaluator pre) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Put, S3KeySubresource.None);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var cancellationToken = context.RequestAborted;

        if (!await CheckWritePreconditionsAsync(bucket, key, request, cancellationToken))
        {
            return Results.StatusCode(412);
        }

        var (body, declaredLength) = RequestBodyDecoder.Decode(request);
        var contentSha = request.Headers.TryGetValue("x-amz-content-sha256", out var cShaVal) && !StringValues.IsNullOrEmpty(cShaVal)
            ? cShaVal.ToString()
            : null;
        var declaredSha = body is AwsChunkedStream || contentSha is "UNSIGNED-PAYLOAD" || contentSha?.Length is not 64
            ? null
            : contentSha;
        var declaredMd5OrNull = request.Headers.TryGetValue("Content-MD5", out var md5Val)
            ? S3RequestExtensions.Nullify(md5Val)
            : null;

        var metadata = S3HeaderCodec.ExtractUserMetadata(request.Headers);
        var declaredChecksums = ChecksumHeaders.ParseDeclared(request.Headers);
        if (declaredChecksums is null)
        {
            return http.Map(new BadDigestError("malformed x-amz-checksum-* header (base64 expected)"));
        }

        var taggingHeader = request.Headers.TryGetValue("x-amz-tagging", out var tagVal)
            ? S3RequestExtensions.Nullify(tagVal)
            : null;
        if (!TagSet.ParseHeader(taggingHeader).TryGetValue(out var initialTags, out var tagError))
        {
            return http.Map(tagError);
        }

        if (!ResolveInitialRetention(request.Headers, bucket).TryGetValue(out var initialRetention, out var retentionError))
        {
            return http.Map(retentionError);
        }

        var initialHold = request.Headers.TryGetValue("x-amz-object-lock-legal-hold", out var holdVal)
            && string.Equals(holdVal, "ON", StringComparison.OrdinalIgnoreCase);

        Result<PutOutcome> result;
        try
        {
            var actor = context.Items.TryGetValue("CallerIdentity", out var callerItem) && callerItem is CallerIdentity callerIdentity
                ? callerIdentity.Username
                : "anonymous";
            var putRequest = CreatePutRequest(
                bucket, key, request, body, declaredLength,
                declaredSha, declaredMd5OrNull, metadata, initialTags,
                declaredChecksums, initialRetention, initialHold, actor,
                cancellationToken);
            result = await objects.Put(putRequest);
        }
        catch (InvalidDataException ex)
        {
            return http.Map(new BadDigestError(ex.Message));
        }

        return result.Match<IResult>(
            putOutcome =>
            {
                response.Headers.ETag = $"\"{putOutcome.Etag}\"";
                ChecksumHeaders.Emit(response.Headers, putOutcome.Checksums, fallbackSha256Hex: putOutcome.Sha256);
                response.Headers["x-amz-version-id"] = putOutcome.VersionId;
                return Results.Ok();
            },
            http.Map);
    }

    private async Task<bool> CheckWritePreconditionsAsync(string bucket, string key, HttpRequest request, CancellationToken cancellationToken)
    {
        var writeConditions = S3HeaderCodec.ExtractWritePreconditions(request.Headers);
        if (!pre.HasWriteConditions(writeConditions))
        {
            return true;
        }

        var existing = objects.Stat(bucket, key);
        var currentEtag = existing is Result<ObjectStat>.Success { Value: var stat } ? stat.Etag : null;
        if (pre.EvaluateForWrite(writeConditions, currentEtag) is Precondition.Failed)
        {
            await request.Body.CopyToAsync(Stream.Null, cancellationToken);
            return false;
        }

        return true;
    }

    private static ObjectPutRequest CreatePutRequest(
        string bucket,
        string key,
        HttpRequest request,
        Stream body,
        long? declaredLength,
        string? declaredSha,
        string? declaredMd5OrNull,
        IReadOnlyDictionary<string, string> metadata,
        IReadOnlyDictionary<string, string> initialTags,
        DeclaredChecksums declaredChecksums,
        Retention? initialRetention,
        bool initialHold,
        string actor,
        CancellationToken cancellationToken)
    {
        var systemHeaders = S3HeaderCodec.ExtractSystemHeaders(request.Headers);
        return new ObjectPutRequest(
            bucket, key, body, declaredLength, request.ContentType,
            declaredSha, declaredMd5OrNull, metadata, initialTags,
            declaredChecksums, initialRetention, initialHold, systemHeaders,
            Protocol: "s3", Actor: actor, Host: request.Host.Value,
            Ct: cancellationToken);
    }

    private Result<Retention?> ResolveInitialRetention(IHeaderDictionary headers, string bucket)
    {
        if (headers.TryGetValue("x-amz-object-lock-mode", out var lockModeVal)
            && headers.TryGetValue("x-amz-object-lock-retain-until-date", out var lockUntilVal)
            && !StringValues.IsNullOrEmpty(lockModeVal)
            && !StringValues.IsNullOrEmpty(lockUntilVal))
        {
            var lockModeHeader = lockModeVal.ToString();
            var lockUntilHeader = lockUntilVal.ToString();
            var mode = lockModeHeader switch
            {
                "GOVERNANCE" => (RetentionMode?)RetentionMode.Governance,
                "COMPLIANCE" => RetentionMode.Compliance,
                _ => null,
            };
            return mode is null
                ? new MalformedXmlError($"unknown x-amz-object-lock-mode '{lockModeHeader}'")
                : !DateTimeOffset.TryParse(lockUntilHeader, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var until)
                    ? new MalformedXmlError($"unparseable x-amz-object-lock-retain-until-date '{lockUntilHeader}'")
                    : (Result<Retention?>)new Retention(mode.Value, until);
        }
        return registry.GetObjectLock(bucket) is Result<ObjectLockConfig?>.Success { Value: { Enabled: true, Default: { } def } }
            ? new Retention(def.Mode, def.ResolveUntil(DateTimeOffset.UtcNow))
            : (Result<Retention?>)(Retention?)null;
    }
}
