using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Native.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native.Adapters;

internal sealed class NativeWebhookAdapter(
    IWebhookStore store,
    IWebhookDeliveryService delivery,
    IWebhookYamlSerializer serializer)
{
    public IResult ListWebhooks(HttpContext context)
    {
        RequestTrace.SetTarget("ListWebhooks");
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

        return Results.Json<List<WebhookDto>>([.. hooks.Select(ToDto)], NativeJsonContext.Default.ListWebhookDto);
    }

    public IResult ExportWebhooksYaml(HttpContext context)
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

        var yaml = serializer.ExportToYaml(hooks);
        return Results.Text(yaml, "text/yaml; charset=utf-8");
    }

    public IResult GetWebhook(string id, HttpContext context)
    {
        RequestTrace.SetTarget("GetWebhook");
        var caller = context.GetCaller();
        if (caller is null)
        {
            return new AccessDeniedError("Unauthorized").ToHttpResult();
        }

        var result = store.GetWebhook(id);
        if (!result.TryGetValue(out var hook, out var error))
        {
            return error.ToHttpResult();
        }

        return Results.Json(ToDto(hook), NativeJsonContext.Default.WebhookDto);
    }

    public async Task<IResult> CreateWebhook(HttpContext context)
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
        if (!result.TryGetValue(out var created, out var err))
        {
            return err.ToHttpResult();
        }

        return Results.Json(ToDto(created), NativeJsonContext.Default.WebhookDto, statusCode: 201);
    }

    public async Task<IResult> UpdateWebhook(string id, HttpContext context)
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
        if (!result.TryGetValue(out var updated, out var err))
        {
            return err.ToHttpResult();
        }

        return Results.Json(ToDto(updated), NativeJsonContext.Default.WebhookDto);
    }

    public IResult DeleteWebhook(string id, HttpContext context)
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
        return result.TryGetError(out var err) ? err.ToHttpResult() : Results.NoContent();
    }

    public async Task<IResult> TestWebhook(string id, HttpContext context)
    {
        RequestTrace.SetTarget("TestWebhook");
        var caller = context.GetCaller();
        if (caller is null)
        {
            return new AccessDeniedError("Unauthorized").ToHttpResult();
        }

        var result = await delivery.TestWebhook(id, context.RequestAborted);
        if (!result.TryGetValue(out var testResult, out var err))
        {
            return err.ToHttpResult();
        }

        return Results.Json(
            new WebhookTestResultDto(
                testResult.WebhookId,
                testResult.Success,
                testResult.StatusCode,
                testResult.Latency.TotalMilliseconds,
                testResult.ErrorMessage,
                testResult.ResponseBody),
            NativeJsonContext.Default.WebhookTestResultDto);
    }

    private static WebhookDto ToDto(Webhook webhook) =>
        new(webhook.Id, webhook.Name, webhook.Url, webhook.Secret, webhook.EventFilters, webhook.ResourceFilters, webhook.Active, webhook.CreatedAt, webhook.LastTriggeredAt, webhook.LastStatusCode, webhook.LastError, webhook.IsStatic);
}
