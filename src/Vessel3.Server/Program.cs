using System.Diagnostics;
using System.Globalization;
using Vessel3.Server;

var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();

var dataRoot = Environment.GetEnvironmentVariable("VESSEL3_DATA")
    ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataRoot);
var store = new FileObjectStore(dataRoot);

app.MapGet("/", () => "Vessel3");

app.MapPut("/{bucket}/{**key}", async (string bucket, string key, HttpRequest req, CancellationToken ct) =>
{
    var result = await store.Put(bucket, key, req.Body, req.ContentLength, ct);
    return result switch
    {
        Result<long>.Success => Results.Ok(),
        Result<long>.Failure { Error: InvalidPathError } => Results.BadRequest(),
        Result<long>.Failure => Results.StatusCode(500),
        _ => throw new UnreachableException(),
    };
});

app.MapGet("/{bucket}/{**key}", (string bucket, string key) =>
{
    var result = store.Get(bucket, key);
    return result switch
    {
        Result<StoredObject>.Success ok => Results.File(
            ok.Value.Body,
            "application/octet-stream",
            lastModified: ok.Value.LastModified,
            enableRangeProcessing: true),
        Result<StoredObject>.Failure { Error: NotFoundError } => Results.NotFound(),
        Result<StoredObject>.Failure { Error: InvalidPathError } => Results.BadRequest(),
        Result<StoredObject>.Failure => Results.StatusCode(500),
        _ => throw new UnreachableException(),
    };
});

app.MapMethods("/{bucket}/{**key}", ["HEAD"], (string bucket, string key, HttpResponse res) =>
{
    var result = store.Stat(bucket, key);
    if (result is Result<ObjectStat>.Failure { Error: NotFoundError }) return Results.NotFound();
    if (result is Result<ObjectStat>.Failure { Error: InvalidPathError }) return Results.BadRequest();
    if (result is Result<ObjectStat>.Failure) return Results.StatusCode(500);

    var stat = ((Result<ObjectStat>.Success)result).Value;
    res.ContentLength = stat.Size;
    res.ContentType = "application/octet-stream";
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
