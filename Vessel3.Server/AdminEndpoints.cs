using System.Globalization;
using System.Text.Json;
using Vessel3.Server.Lifecycle;
using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server;

internal static class AdminEndpoints
{
    public static async Task RunGc(HttpContext ctx)
    {
        var gc = ctx.RequestServices.GetRequiredService<IGarbageCollector>();
        var blobAgeSec = ParseAgeQuery(ctx.Request.Query, "blob-age", fallback: (long)TimeSpan.FromHours(1).TotalSeconds);
        var uploadAgeSec = ParseAgeQuery(ctx.Request.Query, "upload-age", fallback: (long)TimeSpan.FromDays(7).TotalSeconds);
        var report = gc.Run(TimeSpan.FromSeconds(blobAgeSec), TimeSpan.FromSeconds(uploadAgeSec));
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, report, AdminJsonContext.Default.GcReport, ctx.RequestAborted);
    }

    public static async Task RunLifecycle(HttpContext ctx)
    {
        var sweeper = ctx.RequestServices.GetRequiredService<ILifecycleSweeper>();
        var now = DateTimeOffset.UtcNow;
        if (ctx.Request.Query.TryGetValue("now", out var nowRaw)
            && DateTimeOffset.TryParse(nowRaw.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            now = parsed;
        var report = sweeper.Run(now);
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, report, AdminJsonContext.Default.LifecycleReport, ctx.RequestAborted);
    }
}
