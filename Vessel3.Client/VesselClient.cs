using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Vessel3.Primitives;

namespace Vessel3.Client;

public sealed class VesselClient(HttpClient http, VesselClientOptions? options = null, bool disposeHttpClient = false) : IVesselClient
{
    private readonly VesselClientOptions options = options ?? new VesselClientOptions();

    public VesselClient(string baseUrl, string? accessKey = null, string? secretKey = null, string? bearerToken = null)
        : this(
            new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") },
            new VesselClientOptions(baseUrl, accessKey, secretKey, bearerToken),
            disposeHttpClient: true)
    {
    }

    public void Dispose()
    {
        if (disposeHttpClient)
        {
            http.Dispose();
        }
    }

    public async Task<Result<WhoAmIDto>> WhoAmIAsync(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, "v1/iam/whoami");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.WhoAmIDto, ct);
    }

    public async Task<Result<IReadOnlyList<BucketDto>>> ListBucketsAsync(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, "v1/buckets");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.IReadOnlyListBucketDto, ct);
    }

    public async Task<Result<bool>> CreateBucketAsync(string bucket, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"v1/buckets/{Uri.EscapeDataString(bucket)}");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? true : await ReadError(res, ct);
    }

    Task<Result> IVesselClient.CreateBucketAsync(string bucket, CancellationToken ct) =>
        CreateBucketAsync(bucket, ct).ContinueWith(t => t.Result.Match(b => Result.Ok, err => err), ct);

    public async Task<Result> DeleteBucketAsync(string bucket, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Delete, $"v1/buckets/{Uri.EscapeDataString(bucket)}");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<BucketAccessDto>> GetBucketAccessAsync(string bucket, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, $"v1/buckets/{Uri.EscapeDataString(bucket)}/access");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.BucketAccessDto, ct);
    }

    public async Task<Result> SetBucketAccessAsync(string bucket, BucketAccessDto access, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"v1/buckets/{Uri.EscapeDataString(bucket)}/access");
        req.Content = CreateJsonContent(access, VesselJsonContext.Default.BucketAccessDto);
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<BucketVersioningDto>> GetBucketVersioningAsync(string bucket, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, $"v1/buckets/{Uri.EscapeDataString(bucket)}/versioning");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.BucketVersioningDto, ct);
    }

    public async Task<Result> SetBucketVersioningAsync(string bucket, string status, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"v1/buckets/{Uri.EscapeDataString(bucket)}/versioning");
        req.Content = CreateJsonContent(new BucketVersioningDto(status), VesselJsonContext.Default.BucketVersioningDto);
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<BucketWebsiteDto?>> GetBucketWebsiteAsync(string bucket, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, $"v1/buckets/{Uri.EscapeDataString(bucket)}/website");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return (BucketWebsiteDto?)null;
        }

        var parsed = await ReadJson(res, VesselJsonContext.Default.BucketWebsiteDto, ct);
        return parsed.TryGetValue(out var dto, out var err)
            ? (BucketWebsiteDto?)dto
            : err;
    }

    public async Task<Result> SetBucketWebsiteAsync(string bucket, BucketWebsiteDto website, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"v1/buckets/{Uri.EscapeDataString(bucket)}/website");
        req.Content = CreateJsonContent(website, VesselJsonContext.Default.BucketWebsiteDto);
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result> DeleteBucketWebsiteAsync(string bucket, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Delete, $"v1/buckets/{Uri.EscapeDataString(bucket)}/website");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<ObjectsPageDto>> ListObjectsAsync(
        string bucket,
        string? prefix = null,
        string? delimiter = null,
        string? marker = null,
        int limit = 1000,
        CancellationToken ct = default)
    {
        var q = new List<string>();
        if (!string.IsNullOrEmpty(prefix)) q.Add($"prefix={Uri.EscapeDataString(prefix)}");
        if (!string.IsNullOrEmpty(delimiter)) q.Add($"delimiter={Uri.EscapeDataString(delimiter)}");
        if (!string.IsNullOrEmpty(marker)) q.Add($"marker={Uri.EscapeDataString(marker)}");
        if (limit > 0) q.Add($"limit={limit}");
        var query = q.Count > 0 ? "?" + string.Join('&', q) : string.Empty;

        using var req = CreateRequest(HttpMethod.Get, $"v1/buckets/{Uri.EscapeDataString(bucket)}/objects{query}");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.ObjectsPageDto, ct);
    }

    public async Task<Result<VesselObjectDownload>> GetObjectAsync(string bucket, string key, string? versionId = null, CancellationToken ct = default)
    {
        var query = !string.IsNullOrEmpty(versionId) ? $"?versionId={Uri.EscapeDataString(versionId)}" : string.Empty;
        var req = CreateRequest(HttpMethod.Get, $"v1/buckets/{Uri.EscapeDataString(bucket)}/objects/{EscapeKey(key)}{query}");
        var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!res.IsSuccessStatusCode)
        {
            var err = await ReadError(res, ct);
            res.Dispose();
            req.Dispose();
            return err;
        }

        req.Dispose();
        var stream = await res.Content.ReadAsStreamAsync(ct);
        var contentType = res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var contentLength = res.Content.Headers.ContentLength ?? -1L;
        var etag = res.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;
        var lastModified = res.Content.Headers.LastModified;
        var verId = res.Headers.TryGetValues("X-Vessel-Version-Id", out var v) ? v.FirstOrDefault() : null;

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in res.Headers)
        {
            if (header.Key.StartsWith("X-Vessel-Meta-", StringComparison.OrdinalIgnoreCase))
            {
                var metaKey = header.Key["X-Vessel-Meta-".Length..];
                metadata[metaKey] = string.Join(',', header.Value);
            }
        }

        return new VesselObjectDownload(stream, contentType, contentLength, etag, lastModified, verId, metadata, res);
    }

    public async Task<Result<ObjectSummaryDto>> StatObjectAsync(string bucket, string key, string? versionId = null, CancellationToken ct = default)
    {
        var query = !string.IsNullOrEmpty(versionId) ? $"?versionId={Uri.EscapeDataString(versionId)}" : string.Empty;
        using var req = CreateRequest(HttpMethod.Head, $"v1/buckets/{Uri.EscapeDataString(bucket)}/objects/{EscapeKey(key)}{query}");
        using var res = await http.SendAsync(req, ct);

        if (!res.IsSuccessStatusCode)
        {
            return await ReadError(res, ct);
        }

        var size = res.Content.Headers.ContentLength ?? 0L;
        var etag = res.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;
        var lastModified = res.Content.Headers.LastModified ?? DateTimeOffset.UtcNow;
        var verId = res.Headers.TryGetValues("X-Vessel-Version-Id", out var v) ? v.FirstOrDefault() : null;

        return new ObjectSummaryDto(key, size, etag, lastModified, verId);
    }

    public async Task<Result<PutObjectResultDto>> PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"v1/buckets/{Uri.EscapeDataString(bucket)}/objects/{EscapeKey(key)}");
        req.Content = new StreamContent(content);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/octet-stream");

        if (metadata is not null)
        {
            foreach (var (k, v) in metadata)
            {
                req.Headers.TryAddWithoutValidation($"X-Vessel-Meta-{k}", v);
            }
        }

        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.PutObjectResultDto, ct);
    }

    public async Task<Result> DeleteObjectAsync(string bucket, string key, string? versionId = null, CancellationToken ct = default)
    {
        var query = !string.IsNullOrEmpty(versionId) ? $"?versionId={Uri.EscapeDataString(versionId)}" : string.Empty;
        using var req = CreateRequest(HttpMethod.Delete, $"v1/buckets/{Uri.EscapeDataString(bucket)}/objects/{EscapeKey(key)}{query}");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<IReadOnlyList<UserDto>>> ListUsersAsync(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, "v1/iam/users");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.IReadOnlyListUserDto, ct);
    }

    public async Task<Result<UserDto>> CreateUserAsync(string username, string role = "Member", CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Post, "v1/iam/users");
        req.Content = CreateJsonContent(new CreateUserRequest(username, role), VesselJsonContext.Default.CreateUserRequest);
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.UserDto, ct);
    }

    public async Task<Result> UpdateUserRoleAsync(string userId, string role, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"v1/iam/users/{Uri.EscapeDataString(userId)}/role");
        req.Content = CreateJsonContent(new UpdateUserRoleRequest(role), VesselJsonContext.Default.UpdateUserRoleRequest);
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result> UpdateUserStatusAsync(string userId, string status, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"v1/iam/users/{Uri.EscapeDataString(userId)}/status");
        req.Content = CreateJsonContent(new UpdateUserStatusRequest(status), VesselJsonContext.Default.UpdateUserStatusRequest);
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result> DeleteUserAsync(string userId, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Delete, $"v1/iam/users/{Uri.EscapeDataString(userId)}");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<AccessKeyDto>> CreateAccessKeyAsync(string userId, string? description = null, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Post, $"v1/iam/users/{Uri.EscapeDataString(userId)}/keys");
        var ttlSec = ttl.HasValue ? (long)ttl.Value.TotalSeconds : (long?)null;
        req.Content = CreateJsonContent(new CreateAccessKeyRequest(description, ttlSec), VesselJsonContext.Default.CreateAccessKeyRequest);
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.AccessKeyDto, ct);
    }

    public async Task<Result<IReadOnlyList<AccessKeyDto>>> ListAccessKeysAsync(string userId, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, $"v1/iam/users/{Uri.EscapeDataString(userId)}/keys");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.IReadOnlyListAccessKeyDto, ct);
    }

    public async Task<Result> RevokeAccessKeyAsync(string accessKeyId, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Delete, $"v1/iam/keys/{Uri.EscapeDataString(accessKeyId)}");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<GcReportDto>> RunGcAsync(long minBlobAgeSec = 3600, long minUploadAgeSec = 604800, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Post, $"v1/admin/gc?minBlobAgeSec={minBlobAgeSec}&minUploadAgeSec={minUploadAgeSec}");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.GcReportDto, ct);
    }

    public async Task<Result<SweepReportDto>> RunSweepAsync(string? nowOverride = null, CancellationToken ct = default)
    {
        var query = !string.IsNullOrEmpty(nowOverride) ? $"?now={Uri.EscapeDataString(nowOverride)}" : string.Empty;
        using var req = CreateRequest(HttpMethod.Post, $"v1/admin/sweep{query}");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.SweepReportDto, ct);
    }

    public async Task<Result<IReadOnlyList<string>>> ListContainerReposAsync(int limit = 100, string? last = null, CancellationToken ct = default)
    {
        var query = $"?n={limit}" + (last is not null ? $"&last={Uri.EscapeDataString(last)}" : "");
        using var req = CreateRequest(HttpMethod.Get, $"v2/_catalog{query}");
        using var res = await http.SendAsync(req, ct);
        var parsed = await ReadJson(res, VesselJsonContext.Default.ContainerCatalogDto, ct);
        return parsed.TryGetValue(out var dto, out var err)
            ? new Result<IReadOnlyList<string>>.Success(dto.Repositories)
            : new Result<IReadOnlyList<string>>.Failure(err);
    }

    public async Task<Result<IReadOnlyList<string>>> ListContainerTagsAsync(string repo, int limit = 100, string? last = null, CancellationToken ct = default)
    {
        var query = $"?n={limit}" + (last is not null ? $"&last={Uri.EscapeDataString(last)}" : "");
        using var req = CreateRequest(HttpMethod.Get, $"v2/{Uri.EscapeDataString(repo)}/tags/list{query}");
        using var res = await http.SendAsync(req, ct);
        var parsed = await ReadJson(res, VesselJsonContext.Default.ContainerTagsDto, ct);
        return parsed.TryGetValue(out var dto, out var err)
            ? new Result<IReadOnlyList<string>>.Success(dto.Tags)
            : new Result<IReadOnlyList<string>>.Failure(err);
    }

    public async Task<Result> DeleteContainerManifestAsync(string repo, string reference, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Delete, $"v2/{Uri.EscapeDataString(repo)}/manifests/{Uri.EscapeDataString(reference)}");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(options.BearerToken))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken);
        }
        else if (!string.IsNullOrEmpty(options.AccessKey))
        {
            req.Headers.TryAddWithoutValidation("X-Vessel-Key", options.AccessKey);
            if (!string.IsNullOrEmpty(options.SecretKey))
            {
                req.Headers.TryAddWithoutValidation("X-Vessel-Secret", options.SecretKey);
            }
        }
        return req;
    }

    private static ByteArrayContent CreateJsonContent<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private static async Task<Result<T>> ReadJson<T>(HttpResponseMessage res, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode)
        {
            return await ReadError(res, ct);
        }

        var stream = await res.Content.ReadAsStreamAsync(ct);
        var parsed = await JsonSerializer.DeserializeAsync(stream, typeInfo, ct);
        return parsed is not null ? parsed : new HttpError("SerializationError", "Null response body", (int)res.StatusCode);
    }

    private static async Task<Error> ReadError(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 0)
            {
                var errDto = JsonSerializer.Deserialize(bytes, VesselJsonContext.Default.ErrorDto);
                if (errDto.Error is { Length: > 0 } code)
                {
                    return new HttpError(code, errDto.Message ?? string.Empty, (int)res.StatusCode);
                }

                var ociErr = JsonSerializer.Deserialize(bytes, VesselJsonContext.Default.OciErrorsDto);
                if (ociErr.Errors is { Count: > 0 } errors)
                {
                    var first = errors[0];
                    return new HttpError(first.Code ?? "OciError", first.Message ?? string.Empty, (int)res.StatusCode);
                }
            }
        }
        catch
        {
        }
        return new HttpError("HttpError", $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}", (int)res.StatusCode);
    }

    private static string EscapeKey(string key)
    {
        var segments = key.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }
        return string.Join('/', segments);
    }
}
