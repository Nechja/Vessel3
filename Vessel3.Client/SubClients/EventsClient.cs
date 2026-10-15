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
        var uri = BuildEventsUri(topicFilter, resourceFilter);
        using var request = transport.CreateRequest(HttpMethod.Get, uri);
        using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? currentData = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrEmpty(line))
            {
                var payload = currentData;
                currentData = null;

                if (!string.IsNullOrEmpty(payload) && DeserializeEvent(payload) is { } eventDto)
                {
                    yield return eventDto;
                }
                continue;
            }

            var (field, value) = ParseSseLine(line);
            if (string.Equals(field, "data", StringComparison.Ordinal))
            {
                currentData = value;
            }
        }
    }

    private static string BuildEventsUri(string? topicFilter, string? resourceFilter)
    {
        List<string> queryParameters = new(2);
        if (!string.IsNullOrWhiteSpace(topicFilter))
        {
            queryParameters.Add($"topics={Uri.EscapeDataString(topicFilter)}");
        }
        if (!string.IsNullOrWhiteSpace(resourceFilter))
        {
            queryParameters.Add($"resource={Uri.EscapeDataString(resourceFilter)}");
        }

        return queryParameters.Count > 0
            ? $"{VesselRoutes.EventsStream}?{string.Join('&', queryParameters)}"
            : VesselRoutes.EventsStream;
    }

    private static (string? Field, string? Value) ParseSseLine(string line)
    {
        if (line.StartsWith(':'))
        {
            return (null, null);
        }

        var colonIndex = line.IndexOf(':');
        if (colonIndex <= 0)
        {
            return (null, null);
        }

        return (line[..colonIndex].Trim(), line[(colonIndex + 1)..].TrimStart());
    }

    private static VesselEventDto? DeserializeEvent(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, VesselJsonContext.Default.VesselEventDto);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
