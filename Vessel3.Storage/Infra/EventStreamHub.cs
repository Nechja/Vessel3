using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Vessel3.Storage;

public sealed class EventStreamHub : IEventStreamHub
{
    private sealed class Subscription : IEventSubscription
    {
        private readonly Guid id;
        private readonly EventStreamHub hub;
        private readonly Channel<VesselEvent> channel;

        public Subscription(Guid id, EventStreamHub hub, string? topicFilter, string? resourceFilter)
        {
            this.id = id;
            this.hub = hub;
            TopicFilter = topicFilter;
            ResourceFilter = resourceFilter;
            channel = Channel.CreateBounded<VesselEvent>(new BoundedChannelOptions(1_000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });
        }

        public string? TopicFilter { get; }
        public string? ResourceFilter { get; }
        public ChannelReader<VesselEvent> Reader => channel.Reader;
        public ChannelWriter<VesselEvent> Writer => channel.Writer;

        public void Dispose()
        {
            channel.Writer.TryComplete();
            hub.Unsubscribe(id);
        }
    }

    private readonly ConcurrentDictionary<Guid, Subscription> subscribers = new();

    public int SubscriberCount => subscribers.Count;

    public void Publish(VesselEvent @event)
    {
        foreach (var sub in subscribers.Values)
        {
            if (MatchesTopic(@event.Type, sub.TopicFilter) && MatchesResource(@event.Subject, sub.ResourceFilter))
            {
                sub.Writer.TryWrite(@event);
            }
        }
    }

    public IEventSubscription Subscribe(string? topicFilter = null, string? resourceFilter = null)
    {
        var id = Guid.NewGuid();
        var sub = new Subscription(id, this, topicFilter, resourceFilter);
        subscribers[id] = sub;
        return sub;
    }

    private void Unsubscribe(Guid id)
    {
        subscribers.TryRemove(id, out _);
    }

    public static bool MatchesTopic(string eventType, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter == "*")
        {
            return true;
        }

        var tokens = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (token == "*" || string.Equals(token, eventType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (token.EndsWith('*'))
            {
                var prefix = token[..^1];
                if (eventType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool MatchesResource(string subject, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter == "*")
        {
            return true;
        }

        if (filter.EndsWith('*'))
        {
            var prefix = filter[..^1];
            return subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(subject, filter, StringComparison.OrdinalIgnoreCase);
    }
}
