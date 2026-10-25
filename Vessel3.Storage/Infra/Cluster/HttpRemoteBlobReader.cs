using System.Buffers;
using System.Security.Cryptography;

namespace Vessel3.Storage;

internal sealed class HttpRemoteBlobReader(
    string volumeId,
    string peerEndpoint,
    HttpClient httpClient,
    string? clusterSecret = null) : IBlobReader
{
    private static readonly SearchValues<char> HexLowerChars = SearchValues.Create("0123456789abcdef");

    private readonly string baseEndpoint = peerEndpoint.TrimEnd('/');

    public string VolumeId { get; } = volumeId;

    public async Task<Result<Stream>> OpenBlob(string sha, CancellationToken ct = default)
    {
        if (!IsValidSha(sha))
            return new NotFoundError($"blob {sha}");

        using var request = CreateRequest(HttpMethod.Get, sha);
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new NotFoundError($"blob {sha}");

            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms, ct);
            if (!VerifySha256(ms, sha))
            {
                await ms.DisposeAsync();
                return new NotFoundError($"blob {sha}");
            }

            ms.Position = 0;
            return ms;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new NotFoundError($"blob {sha}");
        }
    }

    public async Task<bool> BlobExists(string sha, CancellationToken ct = default)
    {
        if (!IsValidSha(sha))
            return false;

        using var request = CreateRequest(HttpMethod.Head, sha);
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

    internal static bool IsValidSha(ReadOnlySpan<char> sha) =>
        sha.Length == 64 && !sha.ContainsAnyExcept(HexLowerChars);

    private HttpRequestMessage CreateRequest(HttpMethod method, string sha)
    {
        var req = new HttpRequestMessage(method, $"{baseEndpoint}/_cluster/blobs/{sha}");
        if (!string.IsNullOrEmpty(clusterSecret))
            req.Headers.TryAddWithoutValidation(VesselHeaders.ClusterSecret, clusterSecret);
        return req;
    }

    private static bool VerifySha256(MemoryStream ms, string expectedSha)
    {
        if (!ms.TryGetBuffer(out var segment))
            return false;

        Span<byte> hashBytes = stackalloc byte[32];
        SHA256.HashData(segment.AsSpan(), hashBytes);
        Span<char> hexChars = stackalloc char[64];
        Convert.TryToHexStringLower(hashBytes, hexChars, out _);
        return hexChars.SequenceEqual(expectedSha.AsSpan());
    }
}

internal sealed class HttpRemoteBlobReaderFactory(
    HttpClient httpClient,
    string? clusterSecret = null) : IRemoteBlobReaderFactory
{
    public IBlobReader CreateReader(string nodeId, string peerEndpoint, AdvertisedVolume volume) =>
        new HttpRemoteBlobReader(
            RemoteVolumeTopologySynchronizer.FormatRemoteVolumeId(nodeId, volume.VolumeId),
            peerEndpoint,
            httpClient,
            clusterSecret);
}
