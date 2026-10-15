using Vessel3.Primitives;

namespace Vessel3.Client;

public interface IWebhooksClient
{
    Task<Result<IReadOnlyList<WebhookDto>>> ListWebhooks(CancellationToken ct = default);
    Task<Result<WebhookDto>> GetWebhook(string id, CancellationToken ct = default);
    Task<Result<WebhookDto>> CreateWebhook(CreateWebhookDto dto, CancellationToken ct = default);
    Task<Result<WebhookDto>> UpdateWebhook(string id, UpdateWebhookDto dto, CancellationToken ct = default);
    Task<Result> DeleteWebhook(string id, CancellationToken ct = default);
    Task<Result<WebhookTestResultDto>> TestWebhook(string id, CancellationToken ct = default);
    Task<Result<string>> ExportWebhooksYaml(CancellationToken ct = default);
}
