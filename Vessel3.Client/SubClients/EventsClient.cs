using System.Runtime.CompilerServices;
using System.Text.Json;
using Vessel3.Primitives;

namespace Vessel3.Client;

internal sealed class EventsClient(VesselHttpTransport transport) : IEventsClient
{
    public async IAsyncEnumerable<VesselEventDto> StreamEvents(
        string? topicFilter = null,
        string? resourceFilter = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = VesselRoutes.EventsStream;
        List<string> queryParams = new(2);
        if (!string.IsNullOrWhiteSpace(topicFilter))
        {
            queryParams.Add($"topics={Uri.EscapeDataString(topicFilter)}");
        }
        if (!string.IsNullOrWhiteSpace(resourceFilter))
        {
            queryParams.Add($"resource={Uri.EscapeDataString(resourceFilter)}");
        }
        if (queryParams.Count > 0)
        {
            path += "?" + string.Join('&', queryParams);
        }

        using var req = transport.CreateRequest(HttpMethod.Get, path);
        using var res = await transport.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? eventType = null;
        string? eventId = null;
        string? data = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrEmpty(line))
            {
                var payload = data;
                eventType = null;
                eventId = null;
                data = null;

                if (string.IsNullOrEmpty(payload))
                {
                    continue;
                }

                VesselEventDto? evt = null;
                try
                {
                    evt = JsonSerializer.Deserialize(payload, VesselJsonContext.Default.VesselEventDto);
                }
                catch
                {
                }

                if (evt is not null)
                {
                    yield return evt;
                }

                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
            {
                continue;
            }

            var field = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].TrimStart();

            switch (field)
            {
                case "event":
                    eventType = value;
                    break;
                case "id":
                    eventId = value;
                    break;
                case "data":
                    data = value;
                    break;
            }
        }
    }
}
