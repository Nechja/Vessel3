using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Protocols.Native.Handlers;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/webhooks", static (NativeWebhookHandler handler, HttpContext ctx) => handler.ListWebhooks(ctx));
        endpoints.MapGet("/v1/webhooks/export.yaml", static (NativeWebhookHandler handler, HttpContext ctx) => handler.ExportWebhooksYaml(ctx));
        endpoints.MapGet("/v1/webhooks/{id}", static (NativeWebhookHandler handler, string id, HttpContext ctx) => handler.GetWebhook(id, ctx));
        endpoints.MapPost("/v1/webhooks", static (NativeWebhookHandler handler, HttpContext ctx) => handler.CreateWebhook(ctx));
        endpoints.MapPut("/v1/webhooks/{id}", static (NativeWebhookHandler handler, string id, HttpContext ctx) => handler.UpdateWebhook(id, ctx));
        endpoints.MapDelete("/v1/webhooks/{id}", static (NativeWebhookHandler handler, string id, HttpContext ctx) => handler.DeleteWebhook(id, ctx));
        endpoints.MapPost("/v1/webhooks/{id}/test", static (NativeWebhookHandler handler, string id, HttpContext ctx) => handler.TestWebhook(id, ctx));

        return endpoints;
    }
}
