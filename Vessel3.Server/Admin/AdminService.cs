using System.Globalization;
using System.Text.Json;
using Vessel3.Server.S3;

namespace Vessel3.Server.Admin;

internal interface IAdminService
{
    Task RunGc(HttpContext ctx);
    Task RunCompact(HttpContext ctx);
    Task RunLifecycle(HttpContext ctx);
    Task GetBucketAccess(string bucket, HttpContext ctx);
    Task SetBucketAccess(string bucket, HttpContext ctx);
}

internal sealed class AdminService(
    IGarbageCollector gc,
    ILifecycleSweeper sweeper,
    ICompactor compactor,
    IBucketRegistry registry,
    IHttpResultMapper http) : IAdminService
{
    public AdminService(IBucketRegistry registry, IHttpResultMapper http)
        : this(null!, null!, null!, registry, http) { }

    public async Task RunGc(HttpContext ctx)
    {
        RequestTrace.SetAction("AdminGc");
        var blobAgeSec = RequestHelpers.ParseAgeQuery(ctx.Request.Query, "blob-age", fallback: (long)TimeSpan.FromHours(1).TotalSeconds);
        var uploadAgeSec = RequestHelpers.ParseAgeQuery(ctx.Request.Query, "upload-age", fallback: (long)TimeSpan.FromDays(7).TotalSeconds);
        var report = await gc.Run(TimeSpan.FromSeconds(blobAgeSec), TimeSpan.FromSeconds(uploadAgeSec));
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, report, AdminJsonContext.Default.GcReport, ctx.RequestAborted);
    }

    public async Task RunCompact(HttpContext ctx)
    {
        RequestTrace.SetAction("AdminCompact");
        var minBytes = RequestHelpers.ParseAgeQuery(ctx.Request.Query, "min-bytes", fallback: 0);
        var report = compactor.Run(minBytes);
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, report, AdminJsonContext.Default.CompactionReport, ctx.RequestAborted);
    }

    public async Task RunLifecycle(HttpContext ctx)
    {
        RequestTrace.SetAction("AdminLifecycle");
        var now = DateTimeOffset.UtcNow;
        if (ctx.Request.Query.TryGetValue("now", out var nowRaw)
            && DateTimeOffset.TryParse(nowRaw.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            now = parsed;
        var report = sweeper.Run(now);
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, report, AdminJsonContext.Default.LifecycleReport, ctx.RequestAborted);
    }

    public async Task GetBucketAccess(string bucket, HttpContext ctx)
    {
        RequestTrace.SetAction("AdminGetBucketAccess");
        if (!registry.GetAccess(bucket).TryGetValue(out var access, out var err))
        {
            await http.Map(err).ExecuteAsync(ctx);
            return;
        }

        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, access, BucketAccessJsonContext.Default.BucketAccess, ctx.RequestAborted);
    }

    public async Task SetBucketAccess(string bucket, HttpContext ctx)
    {
        RequestTrace.SetAction("AdminSetBucketAccess");
        try
        {
            var access = await JsonSerializer.DeserializeAsync(ctx.Request.Body, BucketAccessJsonContext.Default.BucketAccess, ctx.RequestAborted);
            if (access is null)
            {
                await http.Map(new InvalidArgumentError("Invalid JSON body for BucketAccess")).ExecuteAsync(ctx);
                return;
            }

            if (registry.SetAccess(bucket, access).TryGetError(out var err))
            {
                await http.Map(err).ExecuteAsync(ctx);
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(ctx.Response.Body, access, BucketAccessJsonContext.Default.BucketAccess, ctx.RequestAborted);
        }
        catch (JsonException ex)
        {
            await http.Map(new InvalidArgumentError(ex.Message)).ExecuteAsync(ctx);
        }
    }
}
