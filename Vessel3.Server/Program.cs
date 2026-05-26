using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Vessel3.Server;
using Vessel3.Server.S3;
using Vessel3.Server.Storage;
using static Vessel3.Server.RequestHelpers;

var builder = WebApplication.CreateSlimBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 5L * 1024 * 1024 * 1024;
});

var dataRoot = Environment.GetEnvironmentVariable("VESSEL3_DATA")
    ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataRoot);

var accessKey = Environment.GetEnvironmentVariable("VESSEL3_ACCESS_KEY");
var secretKey = Environment.GetEnvironmentVariable("VESSEL3_SECRET_KEY");
var region = Environment.GetEnvironmentVariable("VESSEL3_REGION") ?? "us-east-1";

IS3XmlWriter xml = new S3XmlWriter();
IHttpResultMapper http = new HttpResultMapper(xml);

builder.Services.AddSingleton(xml);
builder.Services.AddSingleton(http);
builder.Services.AddSingleton(new ServerRegion(region));
builder.Services.AddSingleton(new BlobPoolOptions(Path.Combine(dataRoot, "blobs")));
builder.Services.AddSingleton(new BucketRegistryOptions(dataRoot));
builder.Services.AddSingleton(new MultipartStoreOptions(Path.Combine(dataRoot, "uploads")));
builder.Services.AddSingleton<IBlobPool, BlobPool>();
builder.Services.AddSingleton<IBucketRegistry, BucketRegistry>();
builder.Services.AddSingleton<IObjectStore, ObjectStore>();
builder.Services.AddSingleton<IMultipartStore, MultipartStore>();
builder.Services.AddSingleton<IGarbageCollector, GarbageCollector>();
builder.Services.AddSingleton<ILifecycleSweeper, LifecycleSweeper>();

var lifecycleIntervalSec = long.TryParse(
    Environment.GetEnvironmentVariable("VESSEL3_LIFECYCLE_INTERVAL_SECONDS"),
    NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLcSec)
    ? parsedLcSec : 3600;
builder.Services.AddSingleton(new LifecycleServiceOptions(TimeSpan.FromSeconds(lifecycleIntervalSec)));
builder.Services.AddHostedService<LifecycleService>();
builder.Services.AddSingleton<IBucketLister, BucketLister>();
builder.Services.AddSingleton<IPreconditionEvaluator, PreconditionEvaluator>();
builder.Services.AddSingleton<IS3XmlReader, S3XmlReader>();

builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.GetBucketLocation>();
builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.ListMultipartUploads>();
builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.GetBucketVersioning>();
builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.PutBucketVersioning>();
builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.GetObjectLockConfiguration>();
builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.PutObjectLockConfiguration>();
builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.GetBucketLifecycleConfiguration>();
builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.PutBucketLifecycleConfiguration>();
builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.DeleteBucketLifecycle>();
builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.ListObjectVersions>();
builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.ListObjects>();
builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.CreateBucket>();
builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.DeleteBucket>();
builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.HeadBucket>();
builder.Services.AddSingleton<IS3Action, Vessel3.Server.S3.Bucket.DeleteObjects>();
builder.Services.AddSingleton<IS3ActionDispatcher, S3ActionDispatcher>();

if (accessKey is not null && secretKey is not null)
{
    builder.Services.AddSingleton(new SigV4Options(accessKey, secretKey, region));
    builder.Services.AddSingleton<ISigV4Verifier, SigV4Verifier>();
}
else
{
    builder.Services.AddSingleton<ISigV4Verifier, AlwaysPassVerifier>();
}
builder.Services.AddSingleton<SigV4Middleware>();

var metricsToken = Environment.GetEnvironmentVariable("VESSEL3_METRICS_TOKEN");
var metricsAllowAnonymous = (Environment.GetEnvironmentVariable("VESSEL3_METRICS_ALLOW_ANONYMOUS") ?? "")
    .Equals("true", StringComparison.OrdinalIgnoreCase);

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.Equals("/metrics", StringComparison.Ordinal))
    {
        if (!MetricsRequestAuthorized(ctx, metricsToken, metricsAllowAnonymous))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        var sb = new StringBuilder(4096);
        Metrics.Render(sb);
        ctx.Response.ContentType = Metrics.ContentType;
        await ctx.Response.WriteAsync(sb.ToString(), ctx.RequestAborted);
        return;
    }

    var start = Stopwatch.GetTimestamp();
    try
    {
        await next(ctx);
    }
    finally
    {
        var elapsed = Stopwatch.GetTimestamp() - start;
        var methodIdx = Metrics.MethodIndex(ctx.Request.Method);
        var statusIdx = Metrics.StatusIndex(ctx.Response.StatusCode);
        Metrics.RecordRequest(
            methodIdx, statusIdx, elapsed,
            ctx.Request.ContentLength ?? 0,
            ctx.Response.ContentLength ?? 0);
    }
});

app.UseMiddleware<SigV4Middleware>();

app.MapGet("/", async (HttpResponse res, IS3XmlWriter xml, IBucketRegistry registry, CancellationToken ct) =>
{
    res.ContentType = "application/xml";
    await xml.WriteListBuckets(res.Body, registry.List(), ct);
});

app.MapPut("/_admin/gc", async (
    HttpRequest req, HttpResponse res,
    IGarbageCollector gc,
    CancellationToken ct) =>
{
    var blobAgeSec = ParseAgeQuery(req.Query, "blob-age", fallback: (long)TimeSpan.FromHours(1).TotalSeconds);
    var uploadAgeSec = ParseAgeQuery(req.Query, "upload-age", fallback: (long)TimeSpan.FromDays(7).TotalSeconds);
    var report = gc.Run(TimeSpan.FromSeconds(blobAgeSec), TimeSpan.FromSeconds(uploadAgeSec));
    res.ContentType = "application/json";
    await JsonSerializer.SerializeAsync(res.Body, report, AdminJsonContext.Default.GcReport, ct);
});

app.MapPut("/_admin/lifecycle", async (
    HttpRequest req, HttpResponse res,
    ILifecycleSweeper sweeper,
    CancellationToken ct) =>
{
    var now = DateTimeOffset.UtcNow;
    if (req.Query.TryGetValue("now", out var nowRaw)
        && DateTimeOffset.TryParse(nowRaw.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        now = parsed;
    var report = sweeper.Run(now);
    res.ContentType = "application/json";
    await JsonSerializer.SerializeAsync(res.Body, report, AdminJsonContext.Default.LifecycleReport, ct);
});

app.MapGet("/{bucket}", (string bucket, HttpContext ctx, IS3ActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Get, bucket, ctx));

app.MapPut("/{bucket}", (string bucket, HttpContext ctx, IS3ActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Put, bucket, ctx));

app.MapDelete("/{bucket}", (string bucket, HttpContext ctx, IS3ActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Delete, bucket, ctx));

app.MapPost("/{bucket}", (string bucket, HttpContext ctx, IS3ActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Post, bucket, ctx));

app.MapMethods("/{bucket}", ["HEAD"], (string bucket, HttpContext ctx, IS3ActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Head, bucket, ctx));

app.MapPost("/{bucket}/{**key}", async (
    string bucket, string key,
    HttpRequest req, HttpResponse res,
    IMultipartStore multipart, IS3XmlReader reader, IS3XmlWriter xml, IHttpResultMapper http,
    CancellationToken ct) =>
{
    if (req.Query.ContainsKey("uploads"))
    {
        var contentType = req.ContentType;
        var metadata = ExtractUserMetadata(req.Headers);
        return multipart.Create(bucket, key, contentType, metadata).Match<IResult>(
            outcome =>
            {
                res.ContentType = "application/xml";
                return Results.Stream(async stream =>
                    await xml.WriteInitiateMultipartUploadResult(stream, bucket, key, outcome.UploadId, ct),
                    "application/xml");
            },
            http.Map);
    }

    var uploadId = req.Query["uploadId"].ToString();
    if (!string.IsNullOrEmpty(uploadId))
    {
        var parsed = await reader.ReadCompleteMultipartUploadRequest(req.Body, ct);
        if (parsed is Result<IReadOnlyList<CompletedPart>>.Failure pf) return http.Map(pf.Error);
        var parsedParts = ((Result<IReadOnlyList<CompletedPart>>.Success)parsed).Value;
        var clientParts = parsedParts
            .Select(p => (p.Number, p.Etag, p.Sums)).ToList();

        ChecksumAlgorithm? compositeAlgo = null;
        var sdkAlgo = req.Headers["x-amz-sdk-checksum-algorithm"].ToString();
        if (!string.IsNullOrEmpty(sdkAlgo) && ChecksumAlgorithms.TryParseName(sdkAlgo, out var a)) compositeAlgo = a;
        if (compositeAlgo is null)
        {
            foreach (var p in parsedParts)
            {
                if (p.Sums is null) continue;
                if (p.Sums.Crc32 is not null) { compositeAlgo = ChecksumAlgorithm.Crc32; break; }
                if (p.Sums.Crc32C is not null) { compositeAlgo = ChecksumAlgorithm.Crc32C; break; }
                if (p.Sums.Sha1 is not null) { compositeAlgo = ChecksumAlgorithm.Sha1; break; }
                if (p.Sums.Sha256 is not null) { compositeAlgo = ChecksumAlgorithm.Sha256; break; }
            }
        }

        var completed = await multipart.Complete(uploadId, clientParts, compositeAlgo, ct);
        return completed.Match<IResult>(
            outcome =>
            {
                res.ContentType = "application/xml";
                var partsCount = clientParts.Count;
                return Results.Stream(async stream =>
                    await xml.WriteCompleteMultipartUploadResult(stream, bucket, key, outcome.Etag, outcome.Checksums, partsCount, ct),
                    "application/xml");
            },
            http.Map);
    }

    return http.Map(new InvalidPathError("POST on object requires ?uploads or ?uploadId"));
});

app.MapPut("/{bucket}/{**key}", async (
    string bucket, string key,
    HttpRequest req, HttpResponse res,
    IObjectStore objects, IMultipartStore multipart, IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http, IS3XmlWriter xml, IPreconditionEvaluator pre,
    CancellationToken ct) =>
{
    if (req.Query.ContainsKey("tagging"))
    {
        var parsedTags = await reader.ReadTagging(req.Body, ct);
        if (parsedTags is Result<IReadOnlyDictionary<string, string>>.Failure tf) return http.Map(tf.Error);
        var tagsValue = ((Result<IReadOnlyDictionary<string, string>>.Success)parsedTags).Value;
        var versionIdQ = Nullify(req.Query["versionId"].ToString());
        return objects.PutTagging(bucket, key, versionIdQ, tagsValue).Match<IResult>(
            outcome =>
            {
                if (!string.IsNullOrEmpty(outcome.VersionId))
                    res.Headers["x-amz-version-id"] = outcome.VersionId;
                return Results.Ok();
            },
            http.Map);
    }

    if (req.Query.ContainsKey("retention"))
    {
        var versionId = Nullify(req.Query["versionId"].ToString());
        var bypass = req.Headers["x-amz-bypass-governance-retention"].ToString()
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        var parsed = await reader.ReadRetention(req.Body, ct);
        if (parsed is Result<Retention>.Failure pf) return http.Map(pf.Error);
        var retention = ((Result<Retention>.Success)parsed).Value;
        var resolvedVersion = versionId
            ?? (registry.GetCurrentPut(bucket, key) is Result<PutEntry?>.Success { Value: { } cur } ? cur.VersionId : null);
        return resolvedVersion is null
            ? http.Map(new NoSuchKeyError(key))
            : registry.PutRetention(bucket, key, resolvedVersion, retention, bypass).Match<IResult>(
                _ => Results.Ok(),
                http.Map);
    }

    if (req.Query.ContainsKey("legal-hold"))
    {
        var versionId = Nullify(req.Query["versionId"].ToString());
        var parsed = await reader.ReadLegalHold(req.Body, ct);
        if (parsed is Result<bool>.Failure pf) return http.Map(pf.Error);
        var on = ((Result<bool>.Success)parsed).Value;
        var resolvedVersion = versionId
            ?? (registry.GetCurrentPut(bucket, key) is Result<PutEntry?>.Success { Value: { } cur } ? cur.VersionId : null);
        return resolvedVersion is null
            ? http.Map(new NoSuchKeyError(key))
            : registry.PutLegalHold(bucket, key, resolvedVersion, on).Match<IResult>(
                _ => Results.Ok(),
                http.Map);
    }

    var uploadId = req.Query["uploadId"].ToString();
    var partNumberRaw = req.Query["partNumber"].ToString();
    if (!string.IsNullOrEmpty(uploadId) && !string.IsNullOrEmpty(partNumberRaw))
    {
        if (!int.TryParse(partNumberRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var partNumber))
            return http.Map(new InvalidPartError($"partNumber {partNumberRaw} not an integer"));

        var partCopySource = req.Headers["x-amz-copy-source"].ToString();
        if (!string.IsNullOrEmpty(partCopySource))
        {
            if (!TryParseCopySource(partCopySource, out var srcBucket, out var srcKey))
                return http.Map(new InvalidPathError($"x-amz-copy-source: {partCopySource}"));

            var srcResult = objects.Get(srcBucket, srcKey);
            if (srcResult is Result<StoredObject>.Failure srcFail) return http.Map(srcFail.Error);
            var src = ((Result<StoredObject>.Success)srcResult).Value;

            var rangeHeader = req.Headers["x-amz-copy-source-range"].ToString();
            long copyOffset = 0;
            long copyLength = src.Size;
            if (!string.IsNullOrEmpty(rangeHeader)
                && TryParseByteRange(rangeHeader, src.Size, out var rangeStart, out var rangeEnd))
            {
                copyOffset = rangeStart;
                copyLength = rangeEnd - rangeStart + 1;
            }

            try
            {
                if (copyOffset > 0) src.Body.Seek(copyOffset, SeekOrigin.Begin);
            }
            catch (NotSupportedException)
            {
                src.Body.Dispose();
                return http.Map(new InvalidPathError("source stream not seekable for ranged copy"));
            }

            var copyBody = new BoundedStream(src.Body, copyLength);
            var copyResult = await multipart.UploadPart(uploadId, partNumber, copyBody, copyLength, ChecksumSet.Empty, ct);
            return copyResult.Match<IResult>(
                outcome =>
                {
                    res.ContentType = "application/xml";
                    return Results.Stream(async stream =>
                        await xml.WriteCopyPartResult(stream, outcome.Etag, DateTimeOffset.UtcNow, ct),
                        "application/xml");
                },
                http.Map);
        }

        var (partBody, partLength) = DecodeRequestBody(req);
        var partChecksums = ParseDeclaredChecksums(req.Headers);
        if (partChecksums is null)
            return http.Map(new BadDigestError("malformed x-amz-checksum-* header (base64 expected)"));
        var partResult = await multipart.UploadPart(uploadId, partNumber, partBody, partLength, partChecksums, ct);
        return partResult.Match<IResult>(
            outcome =>
            {
                res.Headers.ETag = $"\"{outcome.Etag}\"";
                EmitChecksumHeaders(res.Headers, outcome.Checksums, fallbackSha256Hex: "");
                return Results.Ok();
            },
            http.Map);
    }

    var copySource = req.Headers["x-amz-copy-source"].ToString();
    if (!string.IsNullOrEmpty(copySource))
    {
        var directive = req.Headers["x-amz-metadata-directive"].ToString();
        var metadataOverride = directive.Equals("REPLACE", StringComparison.OrdinalIgnoreCase)
            ? ExtractUserMetadata(req.Headers)
            : null;

        IReadOnlyDictionary<string, string>? tagsOverride = null;
        var tagDirective = req.Headers["x-amz-tagging-directive"].ToString();
        if (tagDirective.Equals("REPLACE", StringComparison.OrdinalIgnoreCase))
        {
            var headerRaw = req.Headers["x-amz-tagging"].ToString();
            var parsedHdr = TagSet.ParseHeader(headerRaw);
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
    var declaredSha = (body is AwsChunkedStream || contentSha is "UNSIGNED-PAYLOAD" || contentSha.Length is not 64)
        ? null
        : contentSha;
    var declaredMd5 = req.Headers["Content-MD5"].ToString();
    var declaredMd5OrNull = Nullify(declaredMd5);

    var metadata = ExtractUserMetadata(req.Headers);
    var declaredChecksums = ParseDeclaredChecksums(req.Headers);
    if (declaredChecksums is null)
        return http.Map(new BadDigestError("malformed x-amz-checksum-* header (base64 expected)"));

    var taggingHeader = req.Headers["x-amz-tagging"].ToString();
    var parsedTagHdr = TagSet.ParseHeader(taggingHeader);
    if (parsedTagHdr is Result<IReadOnlyDictionary<string, string>>.Failure tagFail) return http.Map(tagFail.Error);
    var initialTags = ((Result<IReadOnlyDictionary<string, string>>.Success)parsedTagHdr).Value;

    Retention? initialRetention = null;
    var lockModeHeader = req.Headers["x-amz-object-lock-mode"].ToString();
    var lockUntilHeader = req.Headers["x-amz-object-lock-retain-until-date"].ToString();
    if (!string.IsNullOrEmpty(lockModeHeader) && !string.IsNullOrEmpty(lockUntilHeader))
    {
        var modeOk = lockModeHeader switch
        {
            "GOVERNANCE" => (RetentionMode?)RetentionMode.Governance,
            "COMPLIANCE" => RetentionMode.Compliance,
            _ => null,
        };
        if (modeOk is null) return http.Map(new MalformedXmlError($"unknown x-amz-object-lock-mode '{lockModeHeader}'"));
        if (!DateTimeOffset.TryParse(lockUntilHeader, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var until))
            return http.Map(new MalformedXmlError($"unparseable x-amz-object-lock-retain-until-date '{lockUntilHeader}'"));
        initialRetention = new Retention(modeOk.Value, until);
    }
    else if (registry.GetObjectLock(bucket) is Result<ObjectLockConfig?>.Success { Value: { Enabled: true, Default: { } def } })
    {
        initialRetention = new Retention(def.Mode, def.ResolveUntil(DateTimeOffset.UtcNow));
    }
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
            EmitChecksumHeaders(res.Headers, put.Checksums, fallbackSha256Hex: put.Sha256);
            res.Headers["x-amz-version-id"] = put.VersionId;
            return Results.Ok();
        },
        http.Map);
});

app.MapGet("/{bucket}/{**key}", async (
    string bucket, string key,
    HttpRequest req, HttpResponse res,
    IObjectStore objects, IMultipartStore multipart, IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http, IPreconditionEvaluator pre,
    CancellationToken ct) =>
{
    if (req.Query.ContainsKey("attributes"))
    {
        var attrVersionId = req.Query["versionId"].ToString();
        var fields = req.Headers["x-amz-object-attributes"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return await objects.GetAttributes(bucket, key, Nullify(attrVersionId)).Match<Task<IResult>>(
            async data =>
            {
                var sha = string.IsNullOrEmpty(data.Sha256)
                    ? null
                    : Convert.ToBase64String(Convert.FromHexString(data.Sha256));
                var attrReq = new ObjectAttributesRequest(
                    WantEtag: fields.Contains("ETag"), Etag: data.Etag,
                    WantChecksum: fields.Contains("Checksum"), ChecksumSha256Base64: sha,
                    WantObjectParts: fields.Contains("ObjectParts"), Parts: data.Parts,
                    WantStorageClass: fields.Contains("StorageClass"),
                    WantObjectSize: fields.Contains("ObjectSize"), Size: data.Size);
                res.ContentType = "application/xml";
                await xml.WriteObjectAttributes(res.Body, attrReq, ct);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
    }

    if (req.Query.ContainsKey("tagging"))
    {
        var versionIdQ = Nullify(req.Query["versionId"].ToString());
        return objects.GetTagging(bucket, key, versionIdQ).Match<IResult>(
            tags =>
            {
                res.ContentType = "application/xml";
                return Results.Stream(async stream => await xml.WriteTagging(stream, tags, ct), "application/xml");
            },
            http.Map);
    }

    if (req.Query.ContainsKey("retention"))
    {
        var versionId = Nullify(req.Query["versionId"].ToString())
            ?? (registry.GetCurrentPut(bucket, key) is Result<PutEntry?>.Success { Value: { } cur } ? cur.VersionId : null);
        return versionId is null
            ? http.Map(new NoSuchKeyError(key))
            : await registry.GetRetention(bucket, key, versionId).Match<Task<IResult>>(
                async ret =>
                {
                    if (ret is null) return http.Map(new NoSuchObjectLockConfigurationError($"{bucket}/{key}"));
                    res.ContentType = "application/xml";
                    await xml.WriteRetention(res.Body, ret, ct);
                    return Results.Empty;
                },
                err => Task.FromResult(http.Map(err)));
    }

    if (req.Query.ContainsKey("legal-hold"))
    {
        var versionId = Nullify(req.Query["versionId"].ToString())
            ?? (registry.GetCurrentPut(bucket, key) is Result<PutEntry?>.Success { Value: { } cur2 } ? cur2.VersionId : null);
        return versionId is null
            ? http.Map(new NoSuchKeyError(key))
            : await registry.GetLegalHold(bucket, key, versionId).Match<Task<IResult>>(
                async on =>
                {
                    res.ContentType = "application/xml";
                    await xml.WriteLegalHold(res.Body, on, ct);
                    return Results.Empty;
                },
                err => Task.FromResult(http.Map(err)));
    }

    var listPartsUploadId = req.Query["uploadId"].ToString();
    var getVersionId = req.Query["versionId"].ToString();
    return !string.IsNullOrEmpty(listPartsUploadId)
        ? await multipart.ListParts(listPartsUploadId).Match<Task<IResult>>(
            async parts =>
            {
                res.ContentType = "application/xml";
                await xml.WriteListParts(res.Body, bucket, key, listPartsUploadId, parts, ct);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)))
        : objects.Get(bucket, key, Nullify(getVersionId)).Match<IResult>(
        ok =>
        {
            var precond = pre.EvaluateForRead(req.Headers, ok.Etag, ok.LastModified);
            if (precond is Precondition.NotModified)
            {
                ok.Body.Dispose();
                return Results.StatusCode(304);
            }
            if (precond is Precondition.Failed)
            {
                ok.Body.Dispose();
                return Results.StatusCode(412);
            }
            res.Headers.ETag = $"\"{ok.Etag}\"";
            foreach (var (k, v) in ok.Metadata) res.Headers[$"x-amz-meta-{k}"] = v;
            EmitSystemHeaders(res.Headers, ok.SystemHeaders);

            var rangeRaw = req.Headers.Range.ToString();
            var isRangedSlice = false;
            if (!string.IsNullOrEmpty(rangeRaw))
            {
                var parsed = RequestHelpers.ParseByteRange(rangeRaw, ok.Size);
                switch (parsed)
                {
                    case RequestHelpers.ByteRange.Unsatisfiable:
                        ok.Body.Dispose();
                        res.Headers["Content-Range"] = $"bytes */{ok.Size.ToString(CultureInfo.InvariantCulture)}";
                        return Results.StatusCode(416);
                    case RequestHelpers.ByteRange.Ignored:
                        req.Headers.Remove("Range");
                        break;
                    case RequestHelpers.ByteRange.Normal n:
                        req.Headers.Range = $"bytes={n.Start.ToString(CultureInfo.InvariantCulture)}-{n.End.ToString(CultureInfo.InvariantCulture)}";
                        isRangedSlice = true;
                        break;
                }
            }

            if (!isRangedSlice)
                EmitChecksumHeaders(res.Headers, ok.Checksums, fallbackSha256Hex: ok.Sha256);

            return Results.File(
                ok.Body,
                ok.ContentType,
                lastModified: ok.LastModified,
                enableRangeProcessing: true);
        },
        http.Map);
});

app.MapMethods("/{bucket}/{**key}", ["HEAD"], (
    string bucket, string key,
    HttpRequest req, HttpResponse res,
    IObjectStore objects, IHttpResultMapper http, IPreconditionEvaluator pre) =>
{
    var headVersionId = req.Query["versionId"].ToString();
    return objects.Stat(bucket, key, Nullify(headVersionId)).Match<IResult>(
        stat =>
        {
            var precond = pre.EvaluateForRead(req.Headers, stat.Etag, stat.LastModified);
            if (precond is Precondition.NotModified) return Results.StatusCode(304);
            if (precond is Precondition.Failed) return Results.StatusCode(412);
            res.ContentLength = stat.Size;
            res.ContentType = stat.ContentType;
            res.Headers.ETag = $"\"{stat.Etag}\"";
            EmitChecksumHeaders(res.Headers, stat.Checksums, fallbackSha256Hex: stat.Sha256);
            res.Headers.LastModified = stat.LastModified.ToString("R", CultureInfo.InvariantCulture);
            foreach (var (k, v) in stat.Metadata) res.Headers[$"x-amz-meta-{k}"] = v;
            EmitSystemHeaders(res.Headers, stat.SystemHeaders);
            return Results.Empty;
        },
        http.Map);
});

app.MapDelete("/{bucket}/{**key}", (
    string bucket, string key,
    HttpRequest req, HttpResponse res,
    IObjectStore objects, IMultipartStore multipart, IHttpResultMapper http) =>
{
    var uploadId = req.Query["uploadId"].ToString();
    if (!string.IsNullOrEmpty(uploadId))
        return multipart.Abort(uploadId).Match<IResult>(_ => Results.NoContent(), http.Map);

    if (req.Query.ContainsKey("tagging"))
    {
        var tagVersionId = Nullify(req.Query["versionId"].ToString());
        return objects.DeleteTagging(bucket, key, tagVersionId).Match<IResult>(
            outcome =>
            {
                if (!string.IsNullOrEmpty(outcome.VersionId))
                    res.Headers["x-amz-version-id"] = outcome.VersionId;
                return Results.NoContent();
            },
            http.Map);
    }

    var delVersionId = req.Query["versionId"].ToString();
    var bypassGovernance = req.Headers["x-amz-bypass-governance-retention"].ToString()
        .Equals("true", StringComparison.OrdinalIgnoreCase);
    var result = string.IsNullOrEmpty(delVersionId)
        ? objects.Delete(bucket, key, bypassGovernance)
        : objects.DeleteVersion(bucket, key, delVersionId, bypassGovernance);

    return result.Match<IResult>(
        outcome =>
        {
            if (outcome.Found && !string.IsNullOrEmpty(outcome.VersionId))
                res.Headers["x-amz-version-id"] = outcome.VersionId;
            if (outcome.IsDeleteMarker)
                res.Headers["x-amz-delete-marker"] = "true";
            return Results.NoContent();
        },
        http.Map);
});

app.Run();

static bool MetricsRequestAuthorized(HttpContext ctx, string? token, bool allowAnonymous)
{
    if (allowAnonymous) return true;

    var remote = ctx.Connection.RemoteIpAddress;
    var fromLoopback = remote is not null && System.Net.IPAddress.IsLoopback(remote);
    if (fromLoopback && string.IsNullOrEmpty(token)) return true;
    if (string.IsNullOrEmpty(token)) return false;

    var auth = ctx.Request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    if (!auth.StartsWith(prefix, StringComparison.Ordinal)) return fromLoopback;
    var presented = auth[prefix.Length..];
    var a = System.Text.Encoding.UTF8.GetBytes(presented);
    var b = System.Text.Encoding.UTF8.GetBytes(token);
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
}

static ChecksumSet? ParseDeclaredChecksums(IHeaderDictionary headers)
{
    string? Decode(string name)
    {
        var raw = headers[name].ToString();
        return string.IsNullOrEmpty(raw)
            ? null
            : ChecksumAlgorithms.Base64ToHex(raw) ?? "__MALFORMED__";
    }
    var c32 = Decode(ChecksumAlgorithms.HeaderCrc32);
    var c32c = Decode(ChecksumAlgorithms.HeaderCrc32C);
    var s1 = Decode(ChecksumAlgorithms.HeaderSha1);
    var s256 = Decode(ChecksumAlgorithms.HeaderSha256);
    return (c32 is "__MALFORMED__" || c32c is "__MALFORMED__" || s1 is "__MALFORMED__" || s256 is "__MALFORMED__")
        ? null
        : new ChecksumSet(c32, c32c, s1, s256);
}

static void EmitChecksumHeaders(IHeaderDictionary headers, ChecksumSet sums, string fallbackSha256Hex)
{
    if (sums.Crc32 is { } c32) headers[ChecksumAlgorithms.HeaderCrc32] = ChecksumAlgorithms.HexToBase64(c32);
    if (sums.Crc32C is { } c32c) headers[ChecksumAlgorithms.HeaderCrc32C] = ChecksumAlgorithms.HexToBase64(c32c);
    if (sums.Sha1 is { } s1) headers[ChecksumAlgorithms.HeaderSha1] = ChecksumAlgorithms.HexToBase64(s1);
    if (sums.Sha256 is { } s256)
        headers[ChecksumAlgorithms.HeaderSha256] = ChecksumAlgorithms.HexToBase64(s256);
    else if (!string.IsNullOrEmpty(fallbackSha256Hex))
        headers[ChecksumAlgorithms.HeaderSha256] = ChecksumAlgorithms.HexToBase64(fallbackSha256Hex);
}
