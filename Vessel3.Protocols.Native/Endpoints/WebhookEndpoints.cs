using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Protocols.Native.Adapters;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/webhooks", static (NativeWebhookAdapter adapter, HttpContext ctx) => adapter.ListWebhooks(ctx));
        endpoints.MapGet("/v1/webhooks/export.yaml", static (NativeWebhookAdapter adapter, HttpContext ctx) => adapter.ExportWebhooksYaml(ctx));
        endpoints.MapGet("/v1/webhooks/{id}", static (NativeWebhookAdapter adapter, string id, HttpContext ctx) => adapter.GetWebhook(id, ctx));
        endpoints.MapPost("/v1/webhooks", static (NativeWebhookAdapter adapter, HttpContext ctx) => adapter.CreateWebhook(ctx));
        endpoints.MapPut("/v1/webhooks/{id}", static (NativeWebhookAdapter adapter, string id, HttpContext ctx) => adapter.UpdateWebhook(id, ctx));
        endpoints.MapDelete("/v1/webhooks/{id}", static (NativeWebhookAdapter adapter, string id, HttpContext ctx) => adapter.DeleteWebhook(id, ctx));
        endpoints.MapPost("/v1/webhooks/{id}/test", static (NativeWebhookAdapter adapter, string id, HttpContext ctx) => adapter.TestWebhook(id, ctx));

        return endpoints;
    }
}
