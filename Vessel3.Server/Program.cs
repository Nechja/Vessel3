using System.Diagnostics;
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
var store = new FileObjectStore(dataRoot, blobs);

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
            var status = f.Error switch
            {
                MissingSecurityHeaderError => 400,
                AuthorizationHeaderMalformedError => 400,
                InvalidAccessKeyIdError => 403,
                SignatureDoesNotMatchError => 403,
                RequestTimeTooSkewedError => 403,
                _ => 403,
            };
            ctx.Response.StatusCode = status;
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
    await xml.WriteListBuckets(res.Body, store.ListBuckets(), ct);
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
    var result = store.ListObjects(req, continuationToken);

    if (result is Result<ListPage>.Failure { Error: NotFoundError }) return Results.NotFound();
    if (result is Result<ListPage>.Failure { Error: InvalidPathError }) return Results.BadRequest();
    if (result is Result<ListPage>.Failure) return Results.StatusCode(500);

    var page = ((Result<ListPage>.Success)result).Value;
    res.ContentType = "application/xml";
    await xml.WriteListObjects(res.Body, req, page, ct);
    return Results.Empty;
});

app.MapPut("/{bucket}", (string bucket) =>
{
    var result = store.CreateBucket(bucket);
    return result switch
    {
        Result<bool>.Success => Results.Ok(),
        Result<bool>.Failure { Error: InvalidPathError } => Results.BadRequest(),
        Result<bool>.Failure => Results.StatusCode(500),
        _ => throw new UnreachableException(),
    };
});

app.MapDelete("/{bucket}", (string bucket) =>
{
    var result = store.DeleteBucket(bucket);
    return result switch
    {
        Result<bool>.Success => Results.NoContent(),
        Result<bool>.Failure { Error: NotFoundError } => Results.NotFound(),
        Result<bool>.Failure { Error: BucketNotEmptyError } => Results.Conflict(),
        Result<bool>.Failure { Error: InvalidPathError } => Results.BadRequest(),
        Result<bool>.Failure => Results.StatusCode(500),
        _ => throw new UnreachableException(),
    };
});

app.MapMethods("/{bucket}", ["HEAD"], (string bucket) =>
{
    var result = store.BucketExists(bucket);
    return result switch
    {
        Result<bool>.Success { Value: true } => Results.Ok(),
        Result<bool>.Success => Results.NotFound(),
        Result<bool>.Failure { Error: InvalidPathError } => Results.BadRequest(),
        Result<bool>.Failure => Results.StatusCode(500),
        _ => throw new UnreachableException(),
    };
});

app.MapPut("/{bucket}/{**key}", async (string bucket, string key, HttpRequest req, HttpResponse res, CancellationToken ct) =>
{
    var isChunked = req.Headers.ContentEncoding.ToString().Contains("aws-chunked", StringComparison.Ordinal)
        || req.Headers["x-amz-content-sha256"].ToString().Contains("STREAMING-", StringComparison.Ordinal);
    var body = isChunked ? (Stream)new AwsChunkedStream(req.Body) : req.Body;
    var declaredLength = isChunked
        ? (long.TryParse(req.Headers["x-amz-decoded-content-length"].ToString(), out var dl) ? dl : (long?)null)
        : req.ContentLength;
    var contentType = req.ContentType;

    var result = await store.Put(bucket, key, body, declaredLength, contentType, ct);
    if (result is Result<PutOutcome>.Failure { Error: NotFoundError }) return Results.NotFound();
    if (result is Result<PutOutcome>.Failure { Error: InvalidPathError }) return Results.BadRequest();
    if (result is Result<PutOutcome>.Failure) return Results.StatusCode(500);

    var put = ((Result<PutOutcome>.Success)result).Value;
    res.Headers.ETag = $"\"{put.Etag}\"";
    res.Headers["x-amz-version-id"] = put.VersionId;
    return Results.Ok();
});

app.MapGet("/{bucket}/{**key}", (string bucket, string key, HttpResponse res) =>
{
    var result = store.Get(bucket, key);
    if (result is Result<StoredObject>.Failure { Error: NotFoundError }) return Results.NotFound();
    if (result is Result<StoredObject>.Failure { Error: InvalidPathError }) return Results.BadRequest();
    if (result is Result<StoredObject>.Failure) return Results.StatusCode(500);

    var ok = ((Result<StoredObject>.Success)result).Value;
    // ETag is SHA256 hex; AWS SDKs assume ETag = MD5 of body and reject otherwise.
    // Expose the SHA256 via the modern additional-checksum header instead.
    res.Headers["x-amz-checksum-sha256"] = ok.Etag;
    return Results.File(
        ok.Body,
        ok.ContentType,
        lastModified: ok.LastModified,
        enableRangeProcessing: true);
});

app.MapMethods("/{bucket}/{**key}", ["HEAD"], (string bucket, string key, HttpResponse res) =>
{
    var result = store.Stat(bucket, key);
    if (result is Result<ObjectStat>.Failure { Error: NotFoundError }) return Results.NotFound();
    if (result is Result<ObjectStat>.Failure { Error: InvalidPathError }) return Results.BadRequest();
    if (result is Result<ObjectStat>.Failure) return Results.StatusCode(500);

    var stat = ((Result<ObjectStat>.Success)result).Value;
    res.ContentLength = stat.Size;
    res.ContentType = stat.ContentType;
    res.Headers.ETag = $"\"{stat.Etag}\"";
    res.Headers.LastModified = stat.LastModified.ToString("R", CultureInfo.InvariantCulture);
    return Results.Empty;
});

app.MapDelete("/{bucket}/{**key}", (string bucket, string key) =>
{
    var result = store.Delete(bucket, key);
    return result switch
    {
        Result<bool>.Success => Results.NoContent(),
        Result<bool>.Failure { Error: InvalidPathError } => Results.BadRequest(),
        Result<bool>.Failure => Results.StatusCode(500),
        _ => throw new UnreachableException(),
    };
});

app.Run();
