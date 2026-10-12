using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Protocols.Native.Handlers;

namespace Vessel3.Protocols.Native.Endpoints;

public static class EventStreamEndpoints
{
    public static IEndpointRouteBuilder MapEventStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/events/stream", static (NativeEventStreamHandler handler, HttpContext ctx, string? topics, string? resource) =>
            handler.StreamEvents(ctx, topics, resource));
        return endpoints;
    }
}
