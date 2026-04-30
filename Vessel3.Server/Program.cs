using System.Globalization;
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
builder.Services.AddSingleton(new BlobPoolOptions(Path.Combine(dataRoot, "blobs")));
builder.Services.AddSingleton(new BucketRegistryOptions(dataRoot));
builder.Services.AddSingleton<IBlobPool, BlobPool>();
builder.Services.AddSingleton<IBucketRegistry, BucketRegistry>();
builder.Services.AddSingleton<IObjectStore, ObjectStore>();
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

app.MapGet("/{bucket}", async (
    string bucket,
    [FromQuery(Name = "prefix")] string? prefix,
    [FromQuery(Name = "delimiter")] string? delimiter,
    [FromQuery(Name = "max-keys")] int? maxKeys,
    [FromQuery(Name = "continuation-token")] string? continuationToken,
    [FromQuery(Name = "start-after")] string? startAfter,
    HttpResponse res,
    IBucketLister lister,
    IS3XmlWriter xml,
    IHttpResultMapper http,
    CancellationToken ct) =>
{
    var req = new ListRequest(bucket, prefix, delimiter, startAfter, Math.Clamp(maxKeys ?? 1000, 1, 1000));
    return await lister.List(req, continuationToken).Match<Task<IResult>>(
        async page =>
        {
            res.ContentType = "application/xml";
            await xml.WriteListObjects(res.Body, req, page, ct);
            return Results.Empty;
        },
        err => Task.FromResult(http.Map(err)));
});

app.MapPut("/{bucket}", (string bucket, IBucketRegistry registry, IHttpResultMapper http) =>
    registry.Create(bucket).Match<IResult>(
        _ => Results.Ok(),
        http.Map));

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
        outcomes.Add(result is Result<bool>.Failure df
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

app.MapPut("/{bucket}/{**key}", async (
    string bucket, string key,
    HttpRequest req, HttpResponse res,
    IObjectStore objects, IHttpResultMapper http, IS3XmlWriter xml, IPreconditionEvaluator pre,
    CancellationToken ct) =>
{
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

    var contentSha = req.Headers["x-amz-content-sha256"].ToString();
    var isChunked = req.Headers.ContentEncoding.ToString().Contains("aws-chunked", StringComparison.Ordinal)
        || contentSha.Contains("STREAMING-", StringComparison.Ordinal);
    var sigCtx = req.HttpContext.Items["sigctx"] as SignatureContext;
    var body = isChunked ? (Stream)new AwsChunkedStream(req.Body, sigCtx) : req.Body;
    var declaredLength = isChunked
        ? (long.TryParse(req.Headers["x-amz-decoded-content-length"].ToString(), out var dl) ? dl : (long?)null)
        : req.ContentLength;
    var declaredSha = (isChunked || contentSha is "UNSIGNED-PAYLOAD" || contentSha.Length is not 64)
        ? null
        : contentSha;

    var metadata = ExtractUserMetadata(req.Headers);

    Result<PutOutcome> result;
    try
    {
        result = await objects.Put(bucket, key, body, declaredLength, req.ContentType, declaredSha, metadata, ct);
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

app.MapGet("/{bucket}/{**key}", (
    string bucket, string key,
    HttpRequest req, HttpResponse res,
    IObjectStore objects, IHttpResultMapper http, IPreconditionEvaluator pre) =>
    objects.Get(bucket, key).Match<IResult>(
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
            res.Headers["x-amz-checksum-sha256"] = Convert.ToBase64String(Convert.FromHexString(ok.Sha256));
            foreach (var (k, v) in ok.Metadata) res.Headers[$"x-amz-meta-{k}"] = v;
            return Results.File(
                ok.Body,
                ok.ContentType,
                lastModified: ok.LastModified,
                enableRangeProcessing: true);
        },
        http.Map));

app.MapMethods("/{bucket}/{**key}", ["HEAD"], (
    string bucket, string key,
    HttpRequest req, HttpResponse res,
    IObjectStore objects, IHttpResultMapper http, IPreconditionEvaluator pre) =>
    objects.Stat(bucket, key).Match<IResult>(
        stat =>
        {
            var precond = pre.EvaluateForRead(req.Headers, stat.Etag, stat.LastModified);
            if (precond is Precondition.NotModified) return Results.StatusCode(304);
            if (precond is Precondition.Failed) return Results.StatusCode(412);
            res.ContentLength = stat.Size;
            res.ContentType = stat.ContentType;
            res.Headers.ETag = $"\"{stat.Etag}\"";
            res.Headers["x-amz-checksum-sha256"] = Convert.ToBase64String(Convert.FromHexString(stat.Sha256));
            res.Headers.LastModified = stat.LastModified.ToString("R", CultureInfo.InvariantCulture);
            foreach (var (k, v) in stat.Metadata) res.Headers[$"x-amz-meta-{k}"] = v;
            return Results.Empty;
        },
        http.Map));

app.MapDelete("/{bucket}/{**key}", (string bucket, string key, IObjectStore objects, IHttpResultMapper http) =>
    objects.Delete(bucket, key).Match<IResult>(
        _ => Results.NoContent(),
        http.Map));

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
