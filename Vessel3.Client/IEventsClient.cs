namespace Vessel3.Client;

public interface IEventsClient
{
    IAsyncEnumerable<VesselEventDto> StreamEvents(string? topicFilter = null, string? resourceFilter = null, CancellationToken ct = default);
}
