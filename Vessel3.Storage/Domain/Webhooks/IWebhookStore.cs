using Vessel3.Primitives;

namespace Vessel3.Storage;

internal interface IWebhookStore : IDisposable
{
    Result<IReadOnlyList<Webhook>> ListWebhooks();
    Result<Webhook?> GetWebhook(string id);
    Result<Webhook> CreateWebhook(CreateWebhookRequest request, bool isStatic = false);
    Result<Webhook> UpdateWebhook(string id, UpdateWebhookRequest request);
    Result DeleteWebhook(string id);
    Result RecordDeliveryResult(string id, DateTimeOffset timestamp, int? statusCode, string? error);
}
