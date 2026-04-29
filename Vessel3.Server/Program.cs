using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Vessel3.Server;
using Vessel3.Server.S3;
using Vessel3.Server.Storage;

var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();

var dataRoot = Environment.GetEnvironmentVariable("VESSEL3_DATA")
    ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataRoot);

var blobs = new BlobPool(Path.Combine(dataRoot, "blobs"));
var xml = new S3XmlWriter();
var http = new HttpResultMapper();
var registry = new BucketRegistry(dataRoot);
var objects = new ObjectStore(registry, blobs);
var lister = new BucketLister(registry);

var accessKey = Environment.GetEnvironmentVariable("VESSEL3_ACCESS_KEY");
var secretKey = Environment.GetEnvironmentVariable("VESSEL3_SECRET_KEY");
var region = Environment.GetEnvironmentVariable("VESSEL3_REGION") ?? "us-east-1";
var verifier = (accessKey is not null && secretKey is not null)
    ? new SigV4Verifier(accessKey, secretKey, region)
    : null;

if (verifier is not null)
{
    app.Use(async (ctx, next) =>
    {
        var result = verifier.Verify(ctx.Request);
        if (result is Result<bool>.Failure f)
        {
            ctx.Response.StatusCode = f.Error.Status;
            ctx.Response.ContentType = "application/xml";
            await xml.WriteError(ctx.Response.Body, f.Error, ctx.Request.Path, Guid.NewGuid().ToString("N"), ctx.RequestAborted);
            return;
        }
        await next();
    });
}

app.MapGet("/", async (HttpResponse res, CancellationToken ct) =>
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

app.MapPut("/{bucket}", (string bucket) =>
    registry.Create(bucket).Match<IResult>(
        _ => Results.Ok(),
        http.Map));

app.MapDelete("/{bucket}", (string bucket) =>
    registry.Delete(bucket).Match<IResult>(
        _ => Results.NoContent(),
        http.Map));

app.MapMethods("/{bucket}", ["HEAD"], (string bucket) =>
    registry.Exists(bucket).Match<IResult>(
        exists => exists ? Results.Ok() : Results.NotFound(),
        http.Map));

app.MapPut("/{bucket}/{**key}", async (string bucket, string key, HttpRequest req, HttpResponse res, CancellationToken ct) =>
{
    var isChunked = req.Headers.ContentEncoding.ToString().Contains("aws-chunked", StringComparison.Ordinal)
        || req.Headers["x-amz-content-sha256"].ToString().Contains("STREAMING-", StringComparison.Ordinal);
    var body = isChunked ? (Stream)new AwsChunkedStream(req.Body) : req.Body;
    var declaredLength = isChunked
        ? (long.TryParse(req.Headers["x-amz-decoded-content-length"].ToString(), out var dl) ? dl : (long?)null)
        : req.ContentLength;

    var result = await objects.Put(bucket, key, body, declaredLength, req.ContentType, ct);
    return result.Match<IResult>(
        put =>
        {
            res.Headers.ETag = $"\"{put.Etag}\"";
            res.Headers["x-amz-version-id"] = put.VersionId;
            return Results.Ok();
        },
        http.Map);
});

app.MapGet("/{bucket}/{**key}", (string bucket, string key, HttpResponse res) =>
    objects.Get(bucket, key).Match<IResult>(
        ok =>
        {
            res.Headers["x-amz-checksum-sha256"] = ok.Etag;
            return Results.File(
                ok.Body,
                ok.ContentType,
                lastModified: ok.LastModified,
                enableRangeProcessing: true);
        },
        http.Map));

app.MapMethods("/{bucket}/{**key}", ["HEAD"], (string bucket, string key, HttpResponse res) =>
    objects.Stat(bucket, key).Match<IResult>(
        stat =>
        {
            res.ContentLength = stat.Size;
            res.ContentType = stat.ContentType;
            res.Headers.ETag = $"\"{stat.Etag}\"";
            res.Headers.LastModified = stat.LastModified.ToString("R", CultureInfo.InvariantCulture);
            return Results.Empty;
        },
        http.Map));

app.MapDelete("/{bucket}/{**key}", (string bucket, string key) =>
    objects.Delete(bucket, key).Match<IResult>(
        _ => Results.NoContent(),
        http.Map));

app.Run();
