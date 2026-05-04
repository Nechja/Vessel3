using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Vessel3.Server;
using Vessel3.Server.S3;
using Vessel3.Server.Storage;

var builder = WebApplication.CreateSlimBuilder(args);

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
builder.Services.AddSingleton<IBucketLister, BucketLister>();
builder.Services.AddSingleton<IPreconditionEvaluator, PreconditionEvaluator>();
builder.Services.AddSingleton<IS3XmlReader, S3XmlReader>();

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

var app = builder.Build();
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
    var blobAgeSec = ParseSeconds(req.Query["x-blob-age"], 0);
    var uploadAgeSec = ParseSeconds(req.Query["x-upload-age"], 7 * 24 * 60 * 60);
    var report = gc.Run(TimeSpan.FromSeconds(blobAgeSec), TimeSpan.FromSeconds(uploadAgeSec));
    res.ContentType = "application/json";
    await JsonSerializer.SerializeAsync(res.Body, report, AdminJsonContext.Default.GcReport, ct);
});

app.MapGet("/{bucket}", async (
    string bucket,
    [FromQuery(Name = "prefix")] string? prefix,
    [FromQuery(Name = "delimiter")] string? delimiter,
    [FromQuery(Name = "max-keys")] int? maxKeys,
    [FromQuery(Name = "continuation-token")] string? continuationToken,
    [FromQuery(Name = "start-after")] string? startAfter,
    [FromQuery(Name = "marker")] string? marker,
    [FromQuery(Name = "list-type")] string? listType,
    HttpRequest req,
    HttpResponse res,
    IBucketLister lister,
    IBucketRegistry registry,
    IS3XmlWriter xml,
    IHttpResultMapper http,
    ServerRegion serverRegion,
    CancellationToken ct) =>
{
    if (req.Query.ContainsKey("location"))
    {
        return await registry.Exists(bucket).Match<Task<IResult>>(
            async exists =>
            {
                if (!exists) return http.Map(new NotFoundError(bucket));
                res.ContentType = "application/xml";
                await xml.WriteLocationConstraint(res.Body, serverRegion.Value, ct);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
    }

    if (req.Query.ContainsKey("uploads"))
    {
        var multipart = req.HttpContext.RequestServices.GetRequiredService<IMultipartStore>();
        return await registry.Exists(bucket).Match<Task<IResult>>(
            async exists =>
            {
                if (!exists) return http.Map(new NotFoundError(bucket));
                res.ContentType = "application/xml";
                await xml.WriteListMultipartUploads(res.Body, bucket, multipart.ListUploads(bucket), ct);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
    }

    if (req.Query.ContainsKey("versioning"))
    {
        return await registry.GetVersioning(bucket).Match<Task<IResult>>(
            async status =>
            {
                res.ContentType = "application/xml";
                await xml.WriteVersioningConfiguration(res.Body, status, ct);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
    }

    if (req.Query.ContainsKey("versions"))
    {
        return await registry.ListAllVersions(bucket, prefix, null).Match<Task<IResult>>(
            async entries =>
            {
                res.ContentType = "application/xml";
                await xml.WriteListVersions(res.Body, bucket, prefix, entries, ct);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
    }

    var isV1 = listType is not "2";
    var effectiveStart = isV1 ? marker : startAfter;
    var listReq = new ListRequest(
        bucket, prefix, delimiter, effectiveStart,
        Math.Clamp(maxKeys ?? 1000, 1, 1000),
        IsV1: isV1, Marker: marker);
    return await lister.List(listReq, isV1 ? null : continuationToken).Match<Task<IResult>>(
        async page =>
        {
            res.ContentType = "application/xml";
            await xml.WriteListObjects(res.Body, listReq, page, ct);
            return Results.Empty;
        },
        err => Task.FromResult(http.Map(err)));
});

app.MapPut("/{bucket}", async (
    string bucket, HttpRequest req,
    IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http,
    CancellationToken ct) =>
{
    if (req.Query.ContainsKey("versioning"))
    {
        var parsed = await reader.ReadVersioningConfiguration(req.Body, ct);
        if (parsed is Result<VersioningStatus>.Failure pf) return http.Map(pf.Error);
        var status = ((Result<VersioningStatus>.Success)parsed).Value;
        return registry.SetVersioning(bucket, status).Match<IResult>(
            _ => Results.Ok(),
            http.Map);
    }

    return registry.Create(bucket).Match<IResult>(
        _ => Results.Ok(),
        http.Map);
});

app.MapDelete("/{bucket}", (string bucket, IBucketRegistry registry, IHttpResultMapper http) =>
    registry.Delete(bucket).Match<IResult>(
        _ => Results.NoContent(),
        http.Map));

app.MapPost("/{bucket}", async (
    string bucket,
    HttpRequest req, HttpResponse res,
    IObjectStore objects, IS3XmlReader reader, IS3XmlWriter xml, IHttpResultMapper http,
    CancellationToken ct) =>
{
    if (!req.Query.ContainsKey("delete"))
        return http.Map(new InvalidPathError("POST requires ?delete"));

    var parsed = await reader.ReadBatchDeleteRequest(req.Body, ct);
    if (parsed is Result<BatchDeleteRequest>.Failure f) return http.Map(f.Error);

    var request = ((Result<BatchDeleteRequest>.Success)parsed).Value;
    var outcomes = new List<BatchDeleteOutcome>(request.Keys.Count);
    foreach (var k in request.Keys)
    {
        var result = objects.Delete(bucket, k.Key);
        outcomes.Add(result is Result<DeleteOutcome>.Failure df
            ? new BatchDeleteOutcome(k.Key, k.VersionId, df.Error)
            : new BatchDeleteOutcome(k.Key, k.VersionId, null));
    }

    res.ContentType = "application/xml";
    return Results.Stream(
        async stream => await xml.WriteBatchDeleteResult(stream, outcomes, request.Quiet, ct),
        "application/xml");
});

app.MapMethods("/{bucket}", ["HEAD"], (string bucket, IBucketRegistry registry, IHttpResultMapper http) =>
    registry.Exists(bucket).Match<IResult>(
        exists => exists ? Results.Ok() : Results.NotFound(),
        http.Map));

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
        var clientParts = ((Result<IReadOnlyList<CompletedPart>>.Success)parsed).Value
            .Select(p => (p.Number, p.Etag)).ToList();

        var completed = await multipart.Complete(uploadId, clientParts, ct);
        return completed.Match<IResult>(
            outcome =>
            {
                res.ContentType = "application/xml";
                return Results.Stream(async stream =>
                    await xml.WriteCompleteMultipartUploadResult(stream, bucket, key, outcome.Etag, ct),
                    "application/xml");
            },
            http.Map);
    }

    return http.Map(new InvalidPathError("POST on object requires ?uploads or ?uploadId"));
});

app.MapPut("/{bucket}/{**key}", async (
    string bucket, string key,
    HttpRequest req, HttpResponse res,
    IObjectStore objects, IMultipartStore multipart, IHttpResultMapper http, IS3XmlWriter xml, IPreconditionEvaluator pre,
    CancellationToken ct) =>
{
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
            var copyResult = await multipart.UploadPart(uploadId, partNumber, copyBody, copyLength, ct);
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
        var partResult = await multipart.UploadPart(uploadId, partNumber, partBody, partLength, ct);
        return partResult.Match<IResult>(
            outcome =>
            {
                res.Headers.ETag = $"\"{outcome.Etag}\"";
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
        return TryParseCopySource(copySource, out var srcBucket, out var srcKey)
            ? objects.Copy(bucket, key, srcBucket, srcKey, req.Headers, metadataOverride).Match<IResult>(
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
            return Results.StatusCode(412);
    }

    var (body, declaredLength) = DecodeRequestBody(req);
    var contentSha = req.Headers["x-amz-content-sha256"].ToString();
    var declaredSha = (body is AwsChunkedStream || contentSha is "UNSIGNED-PAYLOAD" || contentSha.Length is not 64)
        ? null
        : contentSha;
    var declaredMd5 = req.Headers["Content-MD5"].ToString();
    var declaredMd5OrNull = Nullify(declaredMd5);

    var metadata = ExtractUserMetadata(req.Headers);

    Result<PutOutcome> result;
    try
    {
        result = await objects.Put(bucket, key, body, declaredLength, req.ContentType, declaredSha, declaredMd5OrNull, metadata, ct);
    }
    catch (InvalidDataException ex)
    {
        return http.Map(new BadDigestError(ex.Message));
    }

    return result.Match<IResult>(
        put =>
        {
            res.Headers.ETag = $"\"{put.Etag}\"";
            res.Headers["x-amz-checksum-sha256"] = Convert.ToBase64String(Convert.FromHexString(put.Sha256));
            res.Headers["x-amz-version-id"] = put.VersionId;
            return Results.Ok();
        },
        http.Map);
});

app.MapGet("/{bucket}/{**key}", async (
    string bucket, string key,
    HttpRequest req, HttpResponse res,
    IObjectStore objects, IMultipartStore multipart, IS3XmlWriter xml, IHttpResultMapper http, IPreconditionEvaluator pre,
    CancellationToken ct) =>
{
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
            if (!string.IsNullOrEmpty(ok.Sha256))
                res.Headers["x-amz-checksum-sha256"] = Convert.ToBase64String(Convert.FromHexString(ok.Sha256));
            foreach (var (k, v) in ok.Metadata) res.Headers[$"x-amz-meta-{k}"] = v;
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
            if (!string.IsNullOrEmpty(stat.Sha256))
                res.Headers["x-amz-checksum-sha256"] = Convert.ToBase64String(Convert.FromHexString(stat.Sha256));
            res.Headers.LastModified = stat.LastModified.ToString("R", CultureInfo.InvariantCulture);
            foreach (var (k, v) in stat.Metadata) res.Headers[$"x-amz-meta-{k}"] = v;
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

    var delVersionId = req.Query["versionId"].ToString();
    var result = string.IsNullOrEmpty(delVersionId)
        ? objects.Delete(bucket, key)
        : objects.DeleteVersion(bucket, key, delVersionId);

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

static IReadOnlyDictionary<string, string> ExtractUserMetadata(IHeaderDictionary headers)
{
    const string prefix = "x-amz-meta-";
    var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (name, values) in headers)
    {
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
        var key = name[prefix.Length..].ToLowerInvariant();
        if (key.Length is 0) continue;
        meta[key] = values.ToString();
    }
    return meta;
}

static string? Nullify(string? s) => string.IsNullOrEmpty(s) ? null : s;

static long ParseSeconds(Microsoft.Extensions.Primitives.StringValues v, long fallback) =>
    long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

static (Stream Body, long? DeclaredLength) DecodeRequestBody(HttpRequest req)
{
    var contentSha = req.Headers["x-amz-content-sha256"].ToString();
    var isChunked = req.Headers.ContentEncoding.ToString().Contains("aws-chunked", StringComparison.Ordinal)
        || contentSha.Contains("STREAMING-", StringComparison.Ordinal);
    if (!isChunked) return (req.Body, req.ContentLength);

    var sigCtx = req.HttpContext.Items["sigctx"] as SignatureContext;
    var declared = long.TryParse(req.Headers["x-amz-decoded-content-length"].ToString(), out var dl) ? dl : (long?)null;
    return (new AwsChunkedStream(req.Body, sigCtx), declared);
}

static bool TryParseByteRange(string raw, long size, out long start, out long end)
{
    start = 0;
    end = 0;
    const string prefix = "bytes=";
    if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
    var rest = raw[prefix.Length..];
    var dash = rest.IndexOf('-', StringComparison.Ordinal);
    if (dash < 0) return false;
    if (!long.TryParse(rest[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out start)) return false;
    var endStr = rest[(dash + 1)..];
    end = string.IsNullOrEmpty(endStr) ? size - 1
        : long.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e
        : -1;
    return end >= start && end < size;
}

static bool TryParseCopySource(string raw, out string bucket, out string key)
{
    bucket = string.Empty;
    key = string.Empty;
    var trimmed = raw.StartsWith('/') ? raw[1..] : raw;
    var qm = trimmed.IndexOf('?', StringComparison.Ordinal);
    if (qm >= 0) trimmed = trimmed[..qm];
    var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
    if (slash <= 0 || slash == trimmed.Length - 1) return false;
    bucket = trimmed[..slash];
    key = Uri.UnescapeDataString(trimmed[(slash + 1)..]);
    return true;
}
