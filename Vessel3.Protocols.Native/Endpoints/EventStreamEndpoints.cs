using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Protocols.Native.Adapters;

namespace Vessel3.Protocols.Native.Endpoints;

public static class EventStreamEndpoints
{
    public static IEndpointRouteBuilder MapEventStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/events/stream", static (NativeEventStreamAdapter adapter, HttpContext ctx, string? topics, string? resource) =>
            adapter.StreamEvents(ctx, topics, resource));
        return endpoints;
    }
}
