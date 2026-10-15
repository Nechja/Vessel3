using Vessel3.Primitives;

namespace Vessel3.Client;

internal sealed class WebhooksClient(VesselHttpTransport transport) : IWebhooksClient
{
    public async Task<Result<IReadOnlyList<WebhookDto>>> ListWebhooks(CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Get, VesselRoutes.Webhooks);
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.IReadOnlyListWebhookDto, ct);
    }

    public async Task<Result<WebhookDto>> GetWebhook(string id, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Get, $"{VesselRoutes.Webhooks}/{Uri.EscapeDataString(id)}");
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.WebhookDto, ct);
    }

    public async Task<Result<WebhookDto>> CreateWebhook(CreateWebhookDto dto, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Post, VesselRoutes.Webhooks);
        req.Content = transport.CreateJsonContent(dto, VesselJsonContext.Default.CreateWebhookDto);
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.WebhookDto, ct);
    }

    public async Task<Result<WebhookDto>> UpdateWebhook(string id, UpdateWebhookDto dto, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Put, $"{VesselRoutes.Webhooks}/{Uri.EscapeDataString(id)}");
        req.Content = transport.CreateJsonContent(dto, VesselJsonContext.Default.UpdateWebhookDto);
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.WebhookDto, ct);
    }

    public async Task<Result> DeleteWebhook(string id, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Delete, $"{VesselRoutes.Webhooks}/{Uri.EscapeDataString(id)}");
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }

    public async Task<Result<WebhookTestResultDto>> TestWebhook(string id, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Post, $"{VesselRoutes.Webhooks}/{Uri.EscapeDataString(id)}/test");
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.WebhookTestResultDto, ct);
    }

    public async Task<Result<string>> ExportWebhooksYaml(CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Get, $"{VesselRoutes.Webhooks}/export.yaml");
        using var res = await transport.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            return await transport.ReadError(res, ct);
        }
        var yaml = await res.Content.ReadAsStringAsync(ct);
        return yaml;
    }
}
