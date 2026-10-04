using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Primitives;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native.Endpoints;

public static class EventStreamEndpoints
{
    public static IEndpointRouteBuilder MapEventStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/events/stream", StreamEvents);
        return endpoints;
    }

    private static async Task StreamEvents(
        HttpContext context,
        IEventStreamHub hub,
        string? topics = null,
        string? resource = null)
    {
        RequestTrace.SetTarget("StreamEvents");
        var caller = context.GetCaller();
        if (caller is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized", context.RequestAborted);
            return;
        }

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        using var subscription = hub.Subscribe(topics, resource);
        var ct = context.RequestAborted;

        try
        {
            await context.Response.WriteAsync(": connected\n\n", ct);
            await context.Response.Body.FlushAsync(ct);

            using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            var reader = subscription.Reader;

            while (!ct.IsCancellationRequested)
            {
                var readTask = reader.ReadAsync(ct).AsTask();
                var timerTask = heartbeatTimer.WaitForNextTickAsync(ct).AsTask();

                var completed = await Task.WhenAny(readTask, timerTask);
                if (completed == readTask)
                {
                    var @event = await readTask;
                    var eventJson = JsonSerializer.Serialize(@event, NativeJsonContext.Default.VesselEvent);
                    var sseMessage = $"event: {@event.Type}\nid: {@event.Id}\ndata: {eventJson}\n\n";
                    await context.Response.WriteAsync(sseMessage, ct);
                    await context.Response.Body.FlushAsync(ct);
                }
                else
                {
                    await context.Response.WriteAsync(": keep-alive\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Subscriber disconnected cleanly
        }
    }
}
