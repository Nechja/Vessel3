using System.Threading.Channels;

namespace Vessel3.Storage;

public interface IEventSubscription : IDisposable
{
    ChannelReader<VesselEvent> Reader { get; }
}

public interface IEventStreamHub
{
    int SubscriberCount { get; }
    void Publish(VesselEvent @event);
    IEventSubscription Subscribe(string? topicFilter = null, string? resourceFilter = null);
}
