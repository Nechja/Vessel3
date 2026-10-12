namespace Vessel3.Client;

public interface IVesselClient : IDisposable,
    IBucketsClient,
    IObjectsClient,
    IIdentityClient,
    IAdminClient,
    IContainersClient,
    IWebhooksClient,
    IEventsClient
{
    IBucketsClient Buckets { get; }
    IObjectsClient Objects { get; }
    IIdentityClient Identity { get; }
    IAdminClient Admin { get; }
    IContainersClient Containers { get; }
    IWebhooksClient Webhooks { get; }
    IEventsClient Events { get; }
}
