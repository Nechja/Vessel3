using Vessel3.Primitives;

namespace Vessel3.Storage;

internal interface IWebhookDeliveryService
{
    Task<Result<WebhookDeliveryResult>> TestWebhook(string webhookId, CancellationToken ct = default);
}
