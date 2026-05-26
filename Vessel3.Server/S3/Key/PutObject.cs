using System.Globalization;
using Vessel3.Server.Storage;
using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3.Key;

internal sealed class PutObject(IObjectStore objects, IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http, IPreconditionEvaluator pre) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Put, S3KeySubresource.None);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var ct = ctx.RequestAborted;

        var copySource = req.Headers["x-amz-copy-source"].ToString();
        if (!string.IsNullOrEmpty(copySource))
            return CopyFromSource(req, res, bucket, key, copySource, ct);

        if (pre.HasWriteConditions(req.Headers))
        {
            var existing = objects.Stat(bucket, key);
            var currentEtag = existing is Result<ObjectStat>.Success { Value: var stat } ? stat.Etag : null;
            if (pre.EvaluateForWrite(req.Headers, currentEtag) is Precondition.Failed)
            {
                await req.Body.CopyToAsync(Stream.Null, ct);
                return Results.StatusCode(412);
            }
        }

        var (body, declaredLength) = DecodeRequestBody(req);
        var contentSha = req.Headers["x-amz-content-sha256"].ToString();
        var declaredSha = body is AwsChunkedStream || contentSha is "UNSIGNED-PAYLOAD" || contentSha.Length is not 64
            ? null
            : contentSha;
        var declaredMd5OrNull = Nullify(req.Headers["Content-MD5"].ToString());

        var metadata = ExtractUserMetadata(req.Headers);
        var declaredChecksums = ChecksumHeaders.ParseDeclared(req.Headers);
        if (declaredChecksums is null)
            return http.Map(new BadDigestError("malformed x-amz-checksum-* header (base64 expected)"));

        var parsedTagHdr = TagSet.ParseHeader(req.Headers["x-amz-tagging"].ToString());
        if (parsedTagHdr is Result<IReadOnlyDictionary<string, string>>.Failure tagFail) return http.Map(tagFail.Error);
        var initialTags = ((Result<IReadOnlyDictionary<string, string>>.Success)parsedTagHdr).Value;

        var retention = ResolveInitialRetention(req.Headers, bucket);
        if (retention is Result<Retention?>.Failure rf) return http.Map(rf.Error);
        var initialRetention = ((Result<Retention?>.Success)retention).Value;

        var initialHold = req.Headers["x-amz-object-lock-legal-hold"].ToString()
            .Equals("ON", StringComparison.OrdinalIgnoreCase);

        Result<PutOutcome> result;
        try
        {
            var systemHeaders = ExtractSystemHeaders(req.Headers);
            result = await objects.Put(bucket, key, body, declaredLength, req.ContentType, declaredSha, declaredMd5OrNull, metadata, initialTags, declaredChecksums, ct, initialRetention, initialHold, systemHeaders);
        }
        catch (InvalidDataException ex)
        {
            return http.Map(new BadDigestError(ex.Message));
        }

        return result.Match<IResult>(
            put =>
            {
                res.Headers.ETag = $"\"{put.Etag}\"";
                ChecksumHeaders.Emit(res.Headers, put.Checksums, fallbackSha256Hex: put.Sha256);
                res.Headers["x-amz-version-id"] = put.VersionId;
                return Results.Ok();
            },
            http.Map);
    }

    private IResult CopyFromSource(HttpRequest req, HttpResponse res, string bucket, string key, string copySource, CancellationToken ct)
    {
        var directive = req.Headers["x-amz-metadata-directive"].ToString();
        var metadataOverride = directive.Equals("REPLACE", StringComparison.OrdinalIgnoreCase)
            ? ExtractUserMetadata(req.Headers)
            : null;

        IReadOnlyDictionary<string, string>? tagsOverride = null;
        var tagDirective = req.Headers["x-amz-tagging-directive"].ToString();
        if (tagDirective.Equals("REPLACE", StringComparison.OrdinalIgnoreCase))
        {
            var parsedHdr = TagSet.ParseHeader(req.Headers["x-amz-tagging"].ToString());
            if (parsedHdr is Result<IReadOnlyDictionary<string, string>>.Failure tf) return http.Map(tf.Error);
            tagsOverride = ((Result<IReadOnlyDictionary<string, string>>.Success)parsedHdr).Value;
        }

        return TryParseCopySource(copySource, out var srcBucket, out var srcKey)
            ? objects.Copy(bucket, key, srcBucket, srcKey, req.Headers, metadataOverride, tagsOverride).Match<IResult>(
                outcome =>
                {
                    res.Headers["x-amz-copy-source-version-id"] = outcome.VersionId;
                    res.ContentType = "application/xml";
                    return Results.Stream(async stream => await xml.WriteCopyObjectResult(stream, outcome, ct), "application/xml");
                },
                http.Map)
            : http.Map(new InvalidPathError($"x-amz-copy-source: {copySource}"));
    }

    private Result<Retention?> ResolveInitialRetention(IHeaderDictionary headers, string bucket)
    {
        var lockModeHeader = headers["x-amz-object-lock-mode"].ToString();
        var lockUntilHeader = headers["x-amz-object-lock-retain-until-date"].ToString();
        if (!string.IsNullOrEmpty(lockModeHeader) && !string.IsNullOrEmpty(lockUntilHeader))
        {
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
