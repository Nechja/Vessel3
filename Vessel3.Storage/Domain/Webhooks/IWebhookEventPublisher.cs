namespace Vessel3.Storage;

internal interface IWebhookEventPublisher
{
    void Publish(VesselEvent @event);
}
