using System.Security.Cryptography;
using Vessel3.IO;

namespace Vessel3.Storage;

internal sealed class HttpRemoteBlobReader(
    string volumeId,
    string peerEndpoint,
    HttpClient httpClient,
    string? clusterSecret = null) : IBlobReader
{
    private readonly string blobsUrlPrefix = $"{peerEndpoint.TrimEnd('/')}{VesselRoutes.ClusterBlobsPrefix}";

    public string VolumeId { get; } = volumeId;

    public async Task<Result<Stream>> OpenBlob(string sha, CancellationToken ct = default)
    {
        if (!Sha256Hex.IsValid(sha))
            return new NotFoundError($"blob {sha}");

        using var request = CreateRequest(HttpMethod.Get, sha);
        PooledBufferStream? pooled = null;
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new NotFoundError($"blob {sha}");

            await using var netStream = await response.Content.ReadAsStreamAsync(ct);
            pooled = new PooledBufferStream();
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var rented = SohBuffer.Rent();

            int n;
            while ((n = await netStream.ReadAsync(rented.Memory, ct)) > 0)
            {
                var span = rented.Span[..n];
                sha256.AppendData(span);
                pooled.Write(span);
            }

            Span<byte> hashBytes = stackalloc byte[32];
            sha256.GetHashAndReset(hashBytes);
            Span<char> hexChars = stackalloc char[64];
            Convert.TryToHexStringLower(hashBytes, hexChars, out _);

            if (!hexChars.SequenceEqual(sha.AsSpan()))
            {
                await pooled.DisposeAsync();
                return new NotFoundError($"blob {sha}");
            }

            pooled.Position = 0;
            return pooled;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (pooled is not null)
                await pooled.DisposeAsync();
            return new NotFoundError($"blob {sha}");
        }
    }

    public async Task<bool> BlobExists(string sha, CancellationToken ct = default)
    {
        if (!Sha256Hex.IsValid(sha))
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

    private HttpRequestMessage CreateRequest(HttpMethod method, string sha)
    {
        var req = new HttpRequestMessage(method, string.Concat(blobsUrlPrefix, sha));
        if (!string.IsNullOrEmpty(clusterSecret))
            req.Headers.TryAddWithoutValidation(VesselHeaders.ClusterSecret, clusterSecret);
        return req;
    }
}
