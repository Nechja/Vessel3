using System.Net.Http.Headers;
using Vessel3.Primitives;

namespace Vessel3.Client;

internal sealed class ObjectsClient(VesselHttpTransport transport) : IObjectsClient
{
    private const int BufferSize = 65536;
    public const int DefaultPartSize = 5 * 1024 * 1024;

    public async Task<Result<ObjectsPageDto>> ListObjects(
        string bucket,
        string? prefix = null,
        string? delimiter = null,
        string? marker = null,
        int limit = 1000,
        CancellationToken ct = default)
    {
        List<string> queryParameters = [];
        if (!string.IsNullOrEmpty(prefix)) queryParameters.Add($"prefix={Uri.EscapeDataString(prefix)}");
        if (!string.IsNullOrEmpty(delimiter)) queryParameters.Add($"delimiter={Uri.EscapeDataString(delimiter)}");
        if (!string.IsNullOrEmpty(marker)) queryParameters.Add($"marker={Uri.EscapeDataString(marker)}");
        if (limit > 0) queryParameters.Add($"limit={limit}");
        var query = queryParameters.Count > 0 ? "?" + string.Join('&', queryParameters) : string.Empty;

        using var req = transport.CreateRequest(HttpMethod.Get, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/objects{query}");
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.ObjectsPageDto, ct);
    }

    public async Task<Result<VesselObjectDownload>> GetObject(string bucket, string key, string? versionId = null, CancellationToken ct = default)
    {
        var query = !string.IsNullOrEmpty(versionId) ? $"?versionId={Uri.EscapeDataString(versionId)}" : string.Empty;
        var req = transport.CreateRequest(HttpMethod.Get, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/objects/{VesselHttpTransport.EscapeKey(key)}{query}");
        var res = await transport.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!res.IsSuccessStatusCode)
        {
            var err = await transport.ReadError(res, ct);
            res.Dispose();
            req.Dispose();
            return err;
        }

        req.Dispose();
        var stream = await res.Content.ReadAsStreamAsync(ct);
        var contentType = res.Content.Headers.ContentType?.MediaType ?? VesselMediaTypes.OctetStream;
        var contentLength = res.Content.Headers.ContentLength ?? -1L;
        var etag = res.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;
        var lastModified = res.Content.Headers.LastModified;
        var verId = res.Headers.TryGetValues(VesselHeaders.VersionId, out var versionValues) ? versionValues.FirstOrDefault() : null;

        Dictionary<string, string>? metadata = null;
        foreach (var header in res.Headers)
        {
            if (header.Key.StartsWith(VesselHeaders.MetaPrefix, StringComparison.OrdinalIgnoreCase))
            {
                metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var metaKey = header.Key[VesselHeaders.MetaPrefix.Length..];
                metadata[metaKey] = string.Join(',', header.Value);
            }
        }

        return new VesselObjectDownload(stream, contentType, contentLength, etag, lastModified, verId, metadata ?? (IReadOnlyDictionary<string, string>)System.Collections.Frozen.FrozenDictionary<string, string>.Empty, res);
    }

    public async Task<Result<ObjectSummaryDto>> StatObject(string bucket, string key, string? versionId = null, CancellationToken ct = default)
    {
        var query = !string.IsNullOrEmpty(versionId) ? $"?versionId={Uri.EscapeDataString(versionId)}" : string.Empty;
        using var req = transport.CreateRequest(HttpMethod.Head, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/objects/{VesselHttpTransport.EscapeKey(key)}{query}");
        using var res = await transport.SendAsync(req, ct);

        if (!res.IsSuccessStatusCode)
        {
            return await transport.ReadError(res, ct);
        }

        var size = res.Content.Headers.ContentLength ?? 0L;
        var etag = res.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;
        var lastModified = res.Content.Headers.LastModified ?? DateTimeOffset.UtcNow;
        var verId = res.Headers.TryGetValues(VesselHeaders.VersionId, out var versionValues) ? versionValues.FirstOrDefault() : null;

        return new ObjectSummaryDto(key, size, etag, lastModified, verId);
    }

    public Task<Result<PutObjectResultDto>> PutObject(
        string bucket,
        string key,
        Stream content,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => PutObject(bucket, key, content, contentType, metadata, progress: null, ct);

    public async Task<Result<PutObjectResultDto>> PutObject(
        string bucket,
        string key,
        Stream content,
        string? contentType,
        IReadOnlyDictionary<string, string>? metadata,
        IProgress<long>? progress,
        CancellationToken ct = default)
    {
        var uploadStream = progress is not null ? new ProgressStream(content, progress) : content;
        using var req = transport.CreateRequest(HttpMethod.Put, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/objects/{VesselHttpTransport.EscapeKey(key)}");
        req.Content = new StreamContent(uploadStream, BufferSize);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? VesselMediaTypes.OctetStream);

        if (metadata is not null)
        {
            foreach (var (metaKey, metaValue) in metadata)
            {
                req.Headers.TryAddWithoutValidation($"{VesselHeaders.MetaPrefix}{metaKey}", metaValue);
            }
        }

        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.PutObjectResultDto, ct);
    }

    public async Task<Result<CreateUploadResultDto>> CreateMultipartUpload(
        string bucket,
        string key,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Post, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/uploads/{VesselHttpTransport.EscapeKey(key)}");
        if (contentType is not null)
        {
            req.Content = new ByteArrayContent([]);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        if (metadata is not null)
        {
            foreach (var (metaKey, metaValue) in metadata)
            {
                req.Headers.TryAddWithoutValidation($"{VesselHeaders.MetaPrefix}{metaKey}", metaValue);
            }
        }

        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.CreateUploadResultDto, ct);
    }

    public async Task<Result<UploadPartDto>> UploadPart(
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        Stream content,
        long length,
        CancellationToken ct = default)
    {
        var path = $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/uploads/{VesselHttpTransport.EscapeKey(key)}?uploadId={Uri.EscapeDataString(uploadId)}&partNumber={partNumber}";
        using var req = transport.CreateRequest(HttpMethod.Put, path);
        req.Content = new StreamContent(content, BufferSize);
        req.Content.Headers.ContentLength = length;
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(VesselMediaTypes.OctetStream);
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.UploadPartDto, ct);
    }

    public async Task<Result<PutObjectResultDto>> CompleteMultipartUpload(
        string bucket,
        string key,
        string uploadId,
        IReadOnlyList<UploadPartDto> parts,
        CancellationToken ct = default)
    {
        var path = $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/uploads/{VesselHttpTransport.EscapeKey(key)}?uploadId={Uri.EscapeDataString(uploadId)}";
        using var req = transport.CreateRequest(HttpMethod.Post, path);
        req.Content = transport.CreateJsonContent(new CompleteUploadRequestDto(parts), VesselJsonContext.Default.CompleteUploadRequestDto);
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.PutObjectResultDto, ct);
    }

    public async Task<Result> AbortMultipartUpload(
        string bucket,
        string key,
        string uploadId,
        CancellationToken ct = default)
    {
        var path = $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/uploads/{VesselHttpTransport.EscapeKey(key)}?uploadId={Uri.EscapeDataString(uploadId)}";
        using var req = transport.CreateRequest(HttpMethod.Delete, path);
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }

    public async Task<Result<PutObjectResultDto>> UploadObject(
        string bucket,
        string key,
        Stream content,
        long size,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        if (size <= DefaultPartSize)
        {
            return await PutObject(bucket, key, content, contentType, metadata, progress, ct);
        }

        var initRes = await CreateMultipartUpload(bucket, key, contentType, metadata, ct);
        if (!initRes.TryGetValue(out var upload, out var initErr))
        {
            return initErr;
        }

        var parts = new List<UploadPartDto>();
        var partNumber = 1;
        var totalUploaded = 0L;
        var buffer = new byte[DefaultPartSize];

        try
        {
            while (true)
            {
                var bytesRead = 0;
                while (bytesRead < DefaultPartSize)
                {
                    var read = await content.ReadAsync(buffer.AsMemory(bytesRead, DefaultPartSize - bytesRead), ct);
                    if (read == 0) break;
                    bytesRead += read;
                }

                if (bytesRead == 0) break;

                using var partStream = new MemoryStream(buffer, 0, bytesRead, writable: false);
                var partRes = await UploadPart(bucket, key, upload.UploadId, partNumber, partStream, bytesRead, ct);
                if (!partRes.TryGetValue(out var partDto, out var partErr))
                {
                    await AbortMultipartUpload(bucket, key, upload.UploadId, CancellationToken.None);
                    return partErr;
                }

                parts.Add(partDto);
                totalUploaded += bytesRead;
                progress?.Report(totalUploaded);
                partNumber++;

                if (bytesRead < DefaultPartSize) break;
            }

            var completeRes = await CompleteMultipartUpload(bucket, key, upload.UploadId, parts, ct);
            if (!completeRes.TryGetValue(out var outcome, out var completeErr))
            {
                await AbortMultipartUpload(bucket, key, upload.UploadId, CancellationToken.None);
                return completeErr;
            }

            return outcome;
        }
        catch (Exception ex)
        {
            await AbortMultipartUpload(bucket, key, upload.UploadId, CancellationToken.None);
            return new HttpError("ClientError", ex.Message, 500);
        }
    }

    public async Task<Result> DeleteObject(string bucket, string key, string? versionId = null, CancellationToken ct = default)
    {
        var query = !string.IsNullOrEmpty(versionId) ? $"?versionId={Uri.EscapeDataString(versionId)}" : string.Empty;
        using var req = transport.CreateRequest(HttpMethod.Delete, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/objects/{VesselHttpTransport.EscapeKey(key)}{query}");
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }
}
