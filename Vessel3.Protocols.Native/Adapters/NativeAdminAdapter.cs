using System.Globalization;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Native.Serialization;
using Vessel3.Storage;
using Vessel3.Storage.Lifecycle;

namespace Vessel3.Protocols.Native.Adapters;

internal sealed class NativeAdminAdapter(
    IGarbageCollector gc,
    ILifecycleSweeper sweeper,
    IServerLogBuffer buffer)
{
    public async Task<IResult> RunGc(HttpContext ctx)
    {
        RequestTrace.SetTarget("AdminGc");
        var caller = ctx.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError("Admin role required").ToHttpResult();
        }

        var blobAge = long.TryParse(ctx.Request.Query["minBlobAgeSec"], out var b) ? b : 3600;
        var uploadAge = long.TryParse(ctx.Request.Query["minUploadAgeSec"], out var u) ? u : 604800;
        var report = await gc.Run(TimeSpan.FromSeconds(blobAge), TimeSpan.FromSeconds(uploadAge));
        return Results.Json(new GcReportDto(report.BlobsDeleted, report.UploadsReaped), NativeJsonContext.Default.GcReportDto);
    }

    public IResult RunSweep(HttpContext ctx)
    {
        RequestTrace.SetTarget("AdminSweep");
        var caller = ctx.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError("Admin role required").ToHttpResult();
        }

        var now = DateTimeOffset.UtcNow;
        if (ctx.Request.Query.TryGetValue("now", out var nowRaw) &&
            DateTimeOffset.TryParse(nowRaw.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            now = parsed;
        }

        var report = sweeper.Run(now);
        return Results.Json(new SweepReportDto(report.Expired, report.MarkersReaped), NativeJsonContext.Default.SweepReportDto);
    }

    public IResult GetLogs(HttpContext ctx)
    {
        RequestTrace.SetTarget("AdminLogs");
        var caller = ctx.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError("Admin role required").ToHttpResult();
        }

        var limit = int.TryParse(ctx.Request.Query["limit"], out var l) ? l : 100;
        var level = ctx.Request.Query.TryGetValue("level", out var lvl) ? lvl.ToString() : null;
        var protocol = ctx.Request.Query.TryGetValue("protocol", out var proto) ? proto.ToString() : null;

        var logs = buffer.GetRecent(limit, level, protocol);
        List<ServerLogEntryDto> dtos = [.. logs.Select(entry => new ServerLogEntryDto(
            entry.Id,
            entry.Timestamp,
            entry.Level,
            entry.Source,
            entry.Message,
            entry.Protocol,
            entry.Action,
            entry.Subject,
            entry.Actor,
            entry.StatusCode,
            entry.DurationMs,
            entry.TraceId,
            entry.ErrorDetails))];

        return Results.Json(dtos, NativeJsonContext.Default.ListServerLogEntryDto);
    }

    public IResult ClearLogs(HttpContext ctx)
    {
        RequestTrace.SetTarget("AdminClearLogs");
        var caller = ctx.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError("Admin role required").ToHttpResult();
        }

        buffer.Clear();
        return Results.NoContent();
    }
}
