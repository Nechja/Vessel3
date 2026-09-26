using System.Globalization;
using System.Text.Json;
using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server;

internal static class AdminEndpoints
{
    public static async Task RunGc(HttpContext ctx)
    {
        RequestTrace.SetAction("AdminGc");
        var gc = ctx.RequestServices.GetRequiredService<IGarbageCollector>();
        var blobAgeSec = ParseAgeQuery(ctx.Request.Query, "blob-age", fallback: (long)TimeSpan.FromHours(1).TotalSeconds);
        var uploadAgeSec = ParseAgeQuery(ctx.Request.Query, "upload-age", fallback: (long)TimeSpan.FromDays(7).TotalSeconds);
        var report = await gc.Run(TimeSpan.FromSeconds(blobAgeSec), TimeSpan.FromSeconds(uploadAgeSec));
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, report, AdminJsonContext.Default.GcReport, ctx.RequestAborted);
    }

    public static async Task RunCompact(HttpContext ctx)
    {
        RequestTrace.SetAction("AdminCompact");
        var compactor = ctx.RequestServices.GetRequiredService<ICompactor>();
        var minBytes = ParseAgeQuery(ctx.Request.Query, "min-bytes", fallback: 0);
        var report = compactor.Run(minBytes);
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, report, AdminJsonContext.Default.CompactionReport, ctx.RequestAborted);
    }

    public static async Task RunLifecycle(HttpContext ctx)
    {
        RequestTrace.SetAction("AdminLifecycle");
        var sweeper = ctx.RequestServices.GetRequiredService<ILifecycleSweeper>();
        var now = DateTimeOffset.UtcNow;
        if (ctx.Request.Query.TryGetValue("now", out var nowRaw)
            && DateTimeOffset.TryParse(nowRaw.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            now = parsed;
        var report = sweeper.Run(now);
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, report, AdminJsonContext.Default.LifecycleReport, ctx.RequestAborted);
    }

    public static async Task GetBucketAccess(string bucket, HttpContext ctx)
    {
        RequestTrace.SetAction("AdminGetBucketAccess");
        var reg = ctx.RequestServices.GetRequiredService<IBucketRegistry>();
        if (!reg.GetAccess(bucket).TryGetValue(out var access, out var err))
        {
            var http = ctx.RequestServices.GetRequiredService<IHttpResultMapper>();
            await http.Map(err).ExecuteAsync(ctx);
            return;
        }

        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, access, BucketAccessJsonContext.Default.BucketAccess, ctx.RequestAborted);
    }

    public static async Task SetBucketAccess(string bucket, HttpContext ctx)
    {
        RequestTrace.SetAction("AdminSetBucketAccess");
        var reg = ctx.RequestServices.GetRequiredService<IBucketRegistry>();
        var http = ctx.RequestServices.GetRequiredService<IHttpResultMapper>();
        try
        {
            var access = await JsonSerializer.DeserializeAsync(ctx.Request.Body, BucketAccessJsonContext.Default.BucketAccess, ctx.RequestAborted);
            if (access is null)
            {
                await http.Map(new InvalidArgumentError("Invalid JSON body for BucketAccess")).ExecuteAsync(ctx);
                return;
            }

            if (reg.SetAccess(bucket, access).TryGetError(out var err))
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
