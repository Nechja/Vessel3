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
var verifier = (accessKey is not null && secretKey is not null)
    ? new SigV4Verifier(accessKey, secretKey, region)
    : null;

builder.Services.AddSingleton(xml);
builder.Services.AddSingleton(http);
builder.Services.AddSingleton<IBlobPool>(_ => new BlobPool(Path.Combine(dataRoot, "blobs")));
builder.Services.AddSingleton<IBucketRegistry>(_ => new BucketRegistry(dataRoot));
builder.Services.AddSingleton<IObjectStore, ObjectStore>();
builder.Services.AddSingleton<IBucketLister, BucketLister>();
builder.Services.AddSingleton<IPreconditionEvaluator, PreconditionEvaluator>();

var app = builder.Build();

if (verifier is not null)
{
    app.Use(async (ctx, next) =>
    {
        var result = verifier.Verify(ctx.Request);
        if (result is Result<SignatureContext>.Failure f)
        {
            await http.Map(f.Error).ExecuteAsync(ctx);
            return;
        }
        ctx.Items["sigctx"] = ((Result<SignatureContext>.Success)result).Value;
        await next();
    });
}

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
        return TryParseCopySource(copySource, out var srcBucket, out var srcKey)
            ? objects.Copy(bucket, key, srcBucket, srcKey, req.Headers).Match<IResult>(
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

    Result<PutOutcome> result;
    try
    {
        result = await objects.Put(bucket, key, body, declaredLength, req.ContentType, declaredSha, ct);
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
            return Results.Empty;
        },
        http.Map));

app.MapDelete("/{bucket}/{**key}", (string bucket, string key, IObjectStore objects, IHttpResultMapper http) =>
    objects.Delete(bucket, key).Match<IResult>(
        _ => Results.NoContent(),
        http.Map));

app.Run();

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
