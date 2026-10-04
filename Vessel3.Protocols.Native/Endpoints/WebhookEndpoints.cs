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
        endpoints.MapGet("/v1/webhooks", ListWebhooks);
        endpoints.MapGet("/v1/webhooks/export.yaml", ExportWebhooksYaml);
        endpoints.MapGet("/v1/webhooks/{id}", GetWebhook);
        endpoints.MapPost("/v1/webhooks", CreateWebhook);
        endpoints.MapPut("/v1/webhooks/{id}", UpdateWebhook);
        endpoints.MapDelete("/v1/webhooks/{id}", DeleteWebhook);
        endpoints.MapPost("/v1/webhooks/{id}/test", TestWebhook);

        return endpoints;
    }

    private static IResult ListWebhooks(HttpContext context, IWebhookStore store)
    {
        RequestTrace.SetTarget("ListWebhooks");
        var caller = context.GetCaller();
        if (caller is null)
        {
            return new AccessDeniedError("Unauthorized").ToHttpResult();
        }

        var result = store.ListWebhooks();
        return result.Match(
            hooks => Results.Json<List<WebhookDto>>([.. hooks.Select(ToDto)], NativeJsonContext.Default.ListWebhookDto),
            NativeHttpResult.ToHttpResult);
    }

    private static IResult ExportWebhooksYaml(HttpContext context, IWebhookStore store)
    {
        RequestTrace.SetTarget("ExportWebhooksYaml");
        var caller = context.GetCaller();
        if (caller is null)
        {
            return new AccessDeniedError("Unauthorized").ToHttpResult();
        }

        var result = store.ListWebhooks();
        if (!result.TryGetValue(out var hooks, out var error))
        {
            return error.ToHttpResult();
        }

        var yaml = YamlWebhookLoader.ExportToYaml(hooks);
        return Results.Text(yaml, "text/yaml; charset=utf-8");
    }

    private static IResult GetWebhook(string id, HttpContext context, IWebhookStore store)
    {
        RequestTrace.SetTarget("GetWebhook");
        var caller = context.GetCaller();
        if (caller is null)
        {
            return new AccessDeniedError("Unauthorized").ToHttpResult();
        }

        var result = store.GetWebhook(id);
        return result.Match(
            hook => hook is not null
                ? Results.Json(ToDto(hook), NativeJsonContext.Default.WebhookDto)
                : Results.NotFound(),
            NativeHttpResult.ToHttpResult);
    }

    private static async Task<IResult> CreateWebhook(HttpContext context, IWebhookStore store)
    {
        RequestTrace.SetTarget("CreateWebhook");
        var caller = context.GetCaller();
        if (caller is null)
        {
            return new AccessDeniedError("Unauthorized").ToHttpResult();
        }
        if (!caller.CanWrite)
        {
            return new AccessDeniedError("ReadOnly credentials cannot modify webhooks").ToHttpResult();
        }

        var body = await context.Request.ReadFromJsonAsync(NativeJsonContext.Default.CreateWebhookDto);
        var createRequest = new CreateWebhookRequest(body.Name, body.Url, body.Secret, body.EventFilters, body.ResourceFilters, body.Active);
        var result = store.CreateWebhook(createRequest);
        return result.Match(
            created => Results.Json(ToDto(created), NativeJsonContext.Default.WebhookDto, statusCode: 201),
            NativeHttpResult.ToHttpResult);
    }

    private static async Task<IResult> UpdateWebhook(string id, HttpContext context, IWebhookStore store)
    {
        RequestTrace.SetTarget("UpdateWebhook");
        var caller = context.GetCaller();
        if (caller is null)
        {
            return new AccessDeniedError("Unauthorized").ToHttpResult();
        }
        if (!caller.CanWrite)
        {
            return new AccessDeniedError("ReadOnly credentials cannot modify webhooks").ToHttpResult();
        }

        var body = await context.Request.ReadFromJsonAsync(NativeJsonContext.Default.UpdateWebhookDto);
        var updateRequest = new UpdateWebhookRequest(body.Name, body.Url, body.Secret, body.EventFilters, body.ResourceFilters, body.Active);
        var result = store.UpdateWebhook(id, updateRequest);
        return result.Match(
            updated => Results.Json(ToDto(updated), NativeJsonContext.Default.WebhookDto),
            NativeHttpResult.ToHttpResult);
    }

    private static IResult DeleteWebhook(string id, HttpContext context, IWebhookStore store)
    {
        RequestTrace.SetTarget("DeleteWebhook");
        var caller = context.GetCaller();
        if (caller is null)
        {
            return new AccessDeniedError("Unauthorized").ToHttpResult();
        }
        if (!caller.CanWrite)
        {
            return new AccessDeniedError("ReadOnly credentials cannot modify webhooks").ToHttpResult();
        }

        var result = store.DeleteWebhook(id);
        return result.Match(
            () => Results.NoContent(),
            NativeHttpResult.ToHttpResult);
    }

    private static async Task<IResult> TestWebhook(string id, HttpContext context, IWebhookDeliveryService delivery)
    {
        RequestTrace.SetTarget("TestWebhook");
        var caller = context.GetCaller();
        if (caller is null)
        {
            return new AccessDeniedError("Unauthorized").ToHttpResult();
        }

        var result = await delivery.TestWebhook(id, context.RequestAborted);
        return result.Match(
            testResult => Results.Json(
                new WebhookTestResultDto(
                    testResult.WebhookId,
                    testResult.Success,
                    testResult.StatusCode,
                    testResult.Latency.TotalMilliseconds,
                    testResult.ErrorMessage,
                    testResult.ResponseBody),
                NativeJsonContext.Default.WebhookTestResultDto),
            NativeHttpResult.ToHttpResult);
    }

    private static WebhookDto ToDto(Webhook webhook) =>
        new(webhook.Id, webhook.Name, webhook.Url, webhook.Secret, webhook.EventFilters, webhook.ResourceFilters, webhook.Active, webhook.CreatedAt, webhook.LastTriggeredAt, webhook.LastStatusCode, webhook.LastError, webhook.IsStatic);
}
