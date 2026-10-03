using Vessel3.Primitives;

namespace Vessel3.Storage;

internal interface IWebhookDeliveryService
{
    Task<Result<WebhookDeliveryResult>> TestWebhookAsync(string webhookId, CancellationToken ct = default);
}
