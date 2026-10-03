using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Primitives;
using Vessel3.Protocols.Native.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/webhooks", static (HttpContext ctx, IWebhookStore store) =>
        {
            RequestTrace.SetTarget("ListWebhooks");
            var caller = ctx.GetCaller();
            if (caller is null)
                return new AccessDeniedError("Unauthorized").ToHttpResult();

            var result = store.ListWebhooks();
            return result.Match(
                hooks => Results.Json<List<WebhookDto>>([.. hooks.Select(ToDto)], NativeJsonContext.Default.ListWebhookDto),
                NativeHttpResult.ToHttpResult);
        });

        endpoints.MapGet("/v1/webhooks/export.yaml", static (HttpContext ctx, IWebhookStore store) =>
        {
            RequestTrace.SetTarget("ExportWebhooksYaml");
            var caller = ctx.GetCaller();
            if (caller is null)
                return new AccessDeniedError("Unauthorized").ToHttpResult();

            var result = store.ListWebhooks();
            if (!result.TryGetValue(out var hooks, out var err))
                return err.ToHttpResult();

            var yaml = YamlWebhookLoader.ExportToYaml(hooks);
            return Results.Text(yaml, "text/yaml; charset=utf-8");
        });

        endpoints.MapGet("/v1/webhooks/{id}", static (string id, HttpContext ctx, IWebhookStore store) =>
        {
            RequestTrace.SetTarget("GetWebhook");
            var caller = ctx.GetCaller();
            if (caller is null)
                return new AccessDeniedError("Unauthorized").ToHttpResult();

            var result = store.GetWebhook(id);
            return result.Match(
                hook => hook is not null
                    ? Results.Json(ToDto(hook), NativeJsonContext.Default.WebhookDto)
                    : Results.NotFound(),
                NativeHttpResult.ToHttpResult);
        });

        endpoints.MapPost("/v1/webhooks", static async (HttpContext ctx, IWebhookStore store) =>
        {
            RequestTrace.SetTarget("CreateWebhook");
            var caller = ctx.GetCaller();
            if (caller is null)
                return new AccessDeniedError("Unauthorized").ToHttpResult();
            if (!caller.CanWrite)
                return new AccessDeniedError("ReadOnly credentials cannot modify webhooks").ToHttpResult();

            var body = await ctx.Request.ReadFromJsonAsync(NativeJsonContext.Default.CreateWebhookDto);
            var req = new CreateWebhookRequest(body.Name, body.Url, body.Secret, body.EventFilters, body.ResourceFilters, body.Active);
            var result = store.CreateWebhook(req);
            return result.Match(
                created => Results.Json(ToDto(created), NativeJsonContext.Default.WebhookDto, statusCode: 201),
                NativeHttpResult.ToHttpResult);
        });

        endpoints.MapPut("/v1/webhooks/{id}", static async (string id, HttpContext ctx, IWebhookStore store) =>
        {
            RequestTrace.SetTarget("UpdateWebhook");
            var caller = ctx.GetCaller();
            if (caller is null)
                return new AccessDeniedError("Unauthorized").ToHttpResult();
            if (!caller.CanWrite)
                return new AccessDeniedError("ReadOnly credentials cannot modify webhooks").ToHttpResult();

            var body = await ctx.Request.ReadFromJsonAsync(NativeJsonContext.Default.UpdateWebhookDto);
            var req = new UpdateWebhookRequest(body.Name, body.Url, body.Secret, body.EventFilters, body.ResourceFilters, body.Active);
            var result = store.UpdateWebhook(id, req);
            return result.Match(
                updated => Results.Json(ToDto(updated), NativeJsonContext.Default.WebhookDto),
                NativeHttpResult.ToHttpResult);
        });

        endpoints.MapDelete("/v1/webhooks/{id}", static (string id, HttpContext ctx, IWebhookStore store) =>
        {
            RequestTrace.SetTarget("DeleteWebhook");
            var caller = ctx.GetCaller();
            if (caller is null)
                return new AccessDeniedError("Unauthorized").ToHttpResult();
            if (!caller.CanWrite)
                return new AccessDeniedError("ReadOnly credentials cannot modify webhooks").ToHttpResult();

            var result = store.DeleteWebhook(id);
            return result.Match(
                () => Results.NoContent(),
                NativeHttpResult.ToHttpResult);
        });

        endpoints.MapPost("/v1/webhooks/{id}/test", static async (string id, HttpContext ctx, IWebhookDeliveryService delivery) =>
        {
            RequestTrace.SetTarget("TestWebhook");
            var caller = ctx.GetCaller();
            if (caller is null)
                return new AccessDeniedError("Unauthorized").ToHttpResult();

            var result = await delivery.TestWebhookAsync(id, ctx.RequestAborted);
            return result.Match(
                testRes => Results.Json(
                    new WebhookTestResultDto(
                        testRes.WebhookId,
                        testRes.Success,
                        testRes.StatusCode,
                        testRes.Latency.TotalMilliseconds,
                        testRes.ErrorMessage,
                        testRes.ResponseBody),
                    NativeJsonContext.Default.WebhookTestResultDto),
                NativeHttpResult.ToHttpResult);
        });

        return endpoints;
    }

    private static WebhookDto ToDto(Webhook w) =>
        new(w.Id, w.Name, w.Url, w.Secret, w.EventFilters, w.ResourceFilters, w.Active, w.CreatedAt, w.LastTriggeredAt, w.LastStatusCode, w.LastError, w.IsStatic);
}
