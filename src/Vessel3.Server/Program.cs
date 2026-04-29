using System.Diagnostics;
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

app.Run();
