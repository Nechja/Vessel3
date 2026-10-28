using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Vessel3.Storage;

internal sealed class HttpClusterReplicationTransport(
    HttpClient httpClient,
    IClusterMembership membership,
    string? clusterSecret = null) : IClusterReplicationTransport, IClusterUploadResolver
{
    public Task<Result<ClusterCatalogSnapshot>> FetchCatalog(string peerEndpoint, CancellationToken ct = default) =>
        GetJson(
            $"{peerEndpoint.TrimEnd('/')}{VesselRoutes.ClusterCatalogPath}",
            GossipJsonContext.Default.ClusterCatalogSnapshot,
            ct);

    public Task<Result<BucketEventsBatch>> FetchBucketEvents(string peerEndpoint, string bucket, long afterSeq, CancellationToken ct = default) =>
        GetJson(
            $"{peerEndpoint.TrimEnd('/')}{VesselRoutes.ClusterBucketsPrefix}{Uri.EscapeDataString(bucket)}/events?{VesselQueryParams.AfterSeq}={afterSeq.ToString(CultureInfo.InvariantCulture)}",
            GossipJsonContext.Default.BucketEventsBatch,
            ct);

    public async Task<Result<Stream>> OpenBucketSnapshot(string peerEndpoint, string bucket, CancellationToken ct = default)
    {
        var url = $"{peerEndpoint.TrimEnd('/')}{VesselRoutes.ClusterBucketsPrefix}{Uri.EscapeDataString(bucket)}/snapshot";
        using var request = CreateRequest(HttpMethod.Get, url);
        try
        {
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                return new InvalidRequestError($"Snapshot request failed with status {(int)response.StatusCode}.");
            }
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return new ResponseBoundStream(stream, response);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new InvalidRequestError(ex.Message);
        }
    }

    public Task<Result<IdentityReplicationSnapshot>> FetchIamSnapshot(string peerEndpoint, CancellationToken ct = default) =>
        GetJson(
            $"{peerEndpoint.TrimEnd('/')}{VesselRoutes.ClusterIamDeltaPath}",
            GossipJsonContext.Default.IdentityReplicationSnapshot,
            ct);

    public Task<Result<ClusterGcReferencesResponse>> FetchGcReferences(string peerEndpoint, CancellationToken ct = default) =>
        GetJson(
            $"{peerEndpoint.TrimEnd('/')}{VesselRoutes.ClusterGcReferencesPath}",
            GossipJsonContext.Default.ClusterGcReferencesResponse,
            ct);

    public Task<Result<UploadSessionSnapshot>> FetchUploadSession(string peerEndpoint, string sessionId, CancellationToken ct = default) =>
        GetJson(
            $"{peerEndpoint.TrimEnd('/')}{VesselRoutes.ClusterUploadsPrefix}{Uri.EscapeDataString(sessionId)}",
            GossipJsonContext.Default.UploadSessionSnapshot,
            ct);

    public async Task<bool> DeleteUploadSession(string peerEndpoint, string sessionId, CancellationToken ct = default)
    {
        var url = $"{peerEndpoint.TrimEnd('/')}{VesselRoutes.ClusterUploadsPrefix}{Uri.EscapeDataString(sessionId)}";
        using var request = CreateRequest(HttpMethod.Delete, url);
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public IReadOnlyList<UploadSessionSnapshot> FetchPeerSessions(string sessionId)
    {
        var peers = membership.Peers;
        if (peers.Count == 0) return [];

        var results = new List<UploadSessionSnapshot>();
        foreach (var peer in peers)
        {
            if (peer.Status is PeerStatus.Dead or PeerStatus.Left || string.IsNullOrWhiteSpace(peer.Endpoint))
                continue;

            var res = FetchUploadSession(peer.Endpoint, sessionId).GetAwaiter().GetResult();
            if (res.TryGetValue(out var snap, out _))
                results.Add(snap);
        }
        return results;
    }

    public bool DeletePeerSessions(string sessionId)
    {
        var peers = membership.Peers;
        if (peers.Count == 0) return false;

        var anyDeleted = false;
        foreach (var peer in peers)
        {
            if (peer.Status is PeerStatus.Dead or PeerStatus.Left || string.IsNullOrWhiteSpace(peer.Endpoint))
                continue;

            if (DeleteUploadSession(peer.Endpoint, sessionId).GetAwaiter().GetResult())
                anyDeleted = true;
        }
        return anyDeleted;
    }

    private async Task<Result<T>> GetJson<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken ct) where T : class
    {
        using var request = CreateRequest(HttpMethod.Get, url);
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new NoSuchKeyError(url);
            if (!response.IsSuccessStatusCode)
                return new InvalidRequestError($"Cluster request '{url}' failed with status {(int)response.StatusCode}.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var payload = await JsonSerializer.DeserializeAsync(stream, typeInfo, ct);
            return payload is not null
                ? payload
                : new InvalidRequestError($"Empty response from '{url}'.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new InvalidRequestError(ex.Message);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, new Uri(url, UriKind.Absolute));
        if (!string.IsNullOrEmpty(clusterSecret))
            request.Headers.TryAddWithoutValidation(VesselHeaders.ClusterSecret, clusterSecret);
        return request;
    }

    private sealed class ResponseBoundStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            response.Dispose();
            await base.DisposeAsync();
        }
    }
}
