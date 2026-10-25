using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Vessel3.Storage;

internal sealed class HttpGossipTransport(HttpClient httpClient, string? clusterSecret = null) : IGossipTransport
{
    private static readonly MediaTypeHeaderValue JsonMediaType = new(VesselMediaTypes.Json)
    {
        CharSet = VesselMediaTypes.Utf8CharSet
    };

    private readonly ConcurrentDictionary<string, Uri> gossipUris = new(StringComparer.OrdinalIgnoreCase);

    public async Task<Result<GossipMessage>> ExchangeGossip(
        string peerEndpoint,
        GossipMessage outbound,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(peerEndpoint))
            return new InvalidRequestError("Peer endpoint cannot be empty.");

        var uri = gossipUris.GetOrAdd(
            peerEndpoint,
            static ep => new Uri($"{ep.TrimEnd('/')}{VesselRoutes.ClusterGossipPath}", UriKind.Absolute));

        var payload = JsonSerializer.SerializeToUtf8Bytes(outbound, GossipJsonContext.Default.GossipMessage);

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = JsonMediaType;

        if (!string.IsNullOrEmpty(clusterSecret))
            request.Headers.TryAddWithoutValidation(VesselHeaders.ClusterSecret, clusterSecret);

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new InvalidRequestError($"Gossip exchange failed with status {(int)response.StatusCode}.");

            await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            var reply = await JsonSerializer.DeserializeAsync(responseStream, GossipJsonContext.Default.GossipMessage, ct);
            return reply is not null
                ? reply
                : new InvalidRequestError("Peer returned an empty gossip payload.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new InvalidRequestError(ex.Message);
        }
    }
}
