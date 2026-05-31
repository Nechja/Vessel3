using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
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

try
{
    var probe = Path.Combine(dataRoot, ".vessel3-write-test");
    File.WriteAllText(probe, "");
    File.Delete(probe);
}
catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
{
    Console.Error.WriteLine(
        $"VESSEL3_DATA ({dataRoot}) is not writable by the running user. " +
        "On Kubernetes, set the pod securityContext.fsGroup to the runtime uid (1654) " +
        "so the kubelet chowns the volume on mount.");
    return 1;
}

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

builder.Services.AddS3BucketActions();
builder.Services.AddS3KeyActions();

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

var uiAssets = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
var uiContentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
var uiConfig = new UiConfig(accessKey ?? "", secretKey ?? "", region);

app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/_ui", out var remaining))
    {
        await next(ctx);
        return;
    }

    if (accessKey is not null && secretKey is not null && !UiBasicAuthOk(ctx.Request, accessKey, secretKey))
    {
        ctx.Response.StatusCode = 401;
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"vessel3\"";
        return;
    }

    var rel = remaining.HasValue ? remaining.Value!.TrimStart('/') : "";

    if (rel == "config.json")
    {
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, uiConfig, AdminJsonContext.Default.UiConfig, ctx.RequestAborted);
        return;
    }

    if (rel == "admin/gc" && HttpMethods.IsPost(ctx.Request.Method))
    {
        var gc = ctx.RequestServices.GetRequiredService<IGarbageCollector>();
        var blobAge = ParseAgeQuery(ctx.Request.Query, "blob-age", (long)TimeSpan.FromHours(1).TotalSeconds);
        var uploadAge = ParseAgeQuery(ctx.Request.Query, "upload-age", (long)TimeSpan.FromDays(7).TotalSeconds);
        var report = gc.Run(TimeSpan.FromSeconds(blobAge), TimeSpan.FromSeconds(uploadAge));
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, report, AdminJsonContext.Default.GcReport, ctx.RequestAborted);
        return;
    }

    if (rel == "admin/lifecycle" && HttpMethods.IsPost(ctx.Request.Method))
    {
        var sweeper = ctx.RequestServices.GetRequiredService<ILifecycleSweeper>();
        var now = DateTimeOffset.UtcNow;
        if (ctx.Request.Query.TryGetValue("now", out var nowRaw)
            && DateTimeOffset.TryParse(nowRaw.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            now = parsed;
        var report = sweeper.Run(now);
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, report, AdminJsonContext.Default.LifecycleReport, ctx.RequestAborted);
        return;
    }

    if (string.IsNullOrEmpty(rel)) rel = "index.html";
    var info = uiAssets.GetFileInfo(rel);
    if (!info.Exists || info.IsDirectory)
        info = uiAssets.GetFileInfo("index.html");

    ctx.Response.ContentType = uiContentTypes.TryGetContentType(info.Name, out var ct) ? ct : "application/octet-stream";
    ctx.Response.ContentLength = info.Length;
    await using var stream = info.CreateReadStream();
    await stream.CopyToAsync(ctx.Response.Body);
});

static bool UiBasicAuthOk(HttpRequest req, string accessKey, string secretKey)
{
    var auth = req.Headers.Authorization.ToString();
    if (!auth.StartsWith("Basic ", StringComparison.Ordinal)) return false;
    try
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth["Basic ".Length..]));
        var colon = decoded.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0) return false;
        var aUser = Encoding.UTF8.GetBytes(decoded[..colon]);
        var aPass = Encoding.UTF8.GetBytes(decoded[(colon + 1)..]);
        var bUser = Encoding.UTF8.GetBytes(accessKey);
        var bPass = Encoding.UTF8.GetBytes(secretKey);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(aUser, bUser)
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(aPass, bPass);
    }
    catch (FormatException) { return false; }
}

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

app.MapGet("/{bucket}", (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Get, bucket, ctx));

app.MapPut("/{bucket}", (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Put, bucket, ctx));

app.MapDelete("/{bucket}", (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Delete, bucket, ctx));

app.MapPost("/{bucket}", (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Post, bucket, ctx));

app.MapMethods("/{bucket}", ["HEAD"], (string bucket, HttpContext ctx, IS3BucketActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Head, bucket, ctx));

app.MapGet("/{bucket}/{**key}", (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Get, bucket, key, ctx));

app.MapPut("/{bucket}/{**key}", (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Put, bucket, key, ctx));

app.MapDelete("/{bucket}/{**key}", (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Delete, bucket, key, ctx));

app.MapPost("/{bucket}/{**key}", (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Post, bucket, key, ctx));

app.MapMethods("/{bucket}/{**key}", ["HEAD"], (string bucket, string key, HttpContext ctx, IS3KeyActionDispatcher dispatch) =>
    dispatch.Dispatch(HttpMethods.Head, bucket, key, ctx));

app.Run();
return 0;

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

