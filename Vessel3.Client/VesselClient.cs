using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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

    public async Task<Result<WhoAmIDto>> WhoAmI(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, VesselRoutes.WhoAmI);
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.WhoAmIDto, ct);
    }

    public async Task<Result<IReadOnlyList<BucketDto>>> ListBuckets(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, VesselRoutes.Buckets);
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.IReadOnlyListBucketDto, ct);
    }

    public async Task<Result> CreateBucket(string bucket, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result> DeleteBucket(string bucket, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Delete, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<BucketAccessDto>> GetBucketAccess(string bucket, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/access");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.BucketAccessDto, ct);
    }

    public async Task<Result> SetBucketAccess(string bucket, BucketAccessDto access, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/access");
        req.Content = CreateJsonContent(access, VesselJsonContext.Default.BucketAccessDto);
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<BucketVersioningDto>> GetBucketVersioning(string bucket, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/versioning");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.BucketVersioningDto, ct);
    }

    public async Task<Result> SetBucketVersioning(string bucket, string status, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/versioning");
        req.Content = CreateJsonContent(new BucketVersioningDto(status), VesselJsonContext.Default.BucketVersioningDto);
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<BucketWebsiteDto?>> GetBucketWebsite(string bucket, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/website");
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

    public async Task<Result> SetBucketWebsite(string bucket, BucketWebsiteDto website, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/website");
        req.Content = CreateJsonContent(website, VesselJsonContext.Default.BucketWebsiteDto);
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result> DeleteBucketWebsite(string bucket, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Delete, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/website");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

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

        using var req = CreateRequest(HttpMethod.Get, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/objects{query}");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.ObjectsPageDto, ct);
    }

    public async Task<Result<VesselObjectDownload>> GetObject(string bucket, string key, string? versionId = null, CancellationToken ct = default)
    {
        var query = !string.IsNullOrEmpty(versionId) ? $"?versionId={Uri.EscapeDataString(versionId)}" : string.Empty;
        var req = CreateRequest(HttpMethod.Get, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/objects/{EscapeKey(key)}{query}");
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
        using var req = CreateRequest(HttpMethod.Head, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/objects/{EscapeKey(key)}{query}");
        using var res = await http.SendAsync(req, ct);

        if (!res.IsSuccessStatusCode)
        {
            return await ReadError(res, ct);
        }

        var size = res.Content.Headers.ContentLength ?? 0L;
        var etag = res.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;
        var lastModified = res.Content.Headers.LastModified ?? DateTimeOffset.UtcNow;
        var verId = res.Headers.TryGetValues(VesselHeaders.VersionId, out var versionValues) ? versionValues.FirstOrDefault() : null;

        return new ObjectSummaryDto(key, size, etag, lastModified, verId);
    }

    public async Task<Result<PutObjectResultDto>> PutObject(
        string bucket,
        string key,
        Stream content,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/objects/{EscapeKey(key)}");
        req.Content = new StreamContent(content, 81920);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? VesselMediaTypes.OctetStream);

        if (metadata is not null)
        {
            foreach (var (metaKey, metaValue) in metadata)
            {
                req.Headers.TryAddWithoutValidation($"{VesselHeaders.MetaPrefix}{metaKey}", metaValue);
            }
        }

        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.PutObjectResultDto, ct);
    }

    public async Task<Result> DeleteObject(string bucket, string key, string? versionId = null, CancellationToken ct = default)
    {
        var query = !string.IsNullOrEmpty(versionId) ? $"?versionId={Uri.EscapeDataString(versionId)}" : string.Empty;
        using var req = CreateRequest(HttpMethod.Delete, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/objects/{EscapeKey(key)}{query}");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<IReadOnlyList<UserDto>>> ListUsers(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, VesselRoutes.Users);
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.IReadOnlyListUserDto, ct);
    }

    public async Task<Result<UserDto>> CreateUser(string username, string role = "Member", CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Post, VesselRoutes.Users);
        req.Content = CreateJsonContent(new CreateUserRequest(username, role), VesselJsonContext.Default.CreateUserRequest);
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.UserDto, ct);
    }

    public async Task<Result> UpdateUserRole(string userId, string role, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"{VesselRoutes.Users}/{Uri.EscapeDataString(userId)}/role");
        req.Content = CreateJsonContent(new UpdateUserRoleRequest(role), VesselJsonContext.Default.UpdateUserRoleRequest);
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result> UpdateUserStatus(string userId, string status, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"{VesselRoutes.Users}/{Uri.EscapeDataString(userId)}/status");
        req.Content = CreateJsonContent(new UpdateUserStatusRequest(status), VesselJsonContext.Default.UpdateUserStatusRequest);
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result> DeleteUser(string userId, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Delete, $"{VesselRoutes.Users}/{Uri.EscapeDataString(userId)}");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<AccessKeyDto>> CreateAccessKey(string userId, string? description = null, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Post, $"{VesselRoutes.Users}/{Uri.EscapeDataString(userId)}/keys");
        var ttlSec = ttl.HasValue ? (long)ttl.Value.TotalSeconds : (long?)null;
        req.Content = CreateJsonContent(new CreateAccessKeyRequest(description, ttlSec), VesselJsonContext.Default.CreateAccessKeyRequest);
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.AccessKeyDto, ct);
    }

    public async Task<Result<IReadOnlyList<AccessKeyDto>>> ListAccessKeys(string userId, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, $"{VesselRoutes.Users}/{Uri.EscapeDataString(userId)}/keys");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.IReadOnlyListAccessKeyDto, ct);
    }

    public async Task<Result> RevokeAccessKey(string accessKeyId, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Delete, $"{VesselRoutes.Keys}/{Uri.EscapeDataString(accessKeyId)}");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<GcReportDto>> RunGc(long minBlobAgeSec = 3600, long minUploadAgeSec = 604800, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Post, $"{VesselRoutes.Gc}?minBlobAgeSec={minBlobAgeSec}&minUploadAgeSec={minUploadAgeSec}");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.GcReportDto, ct);
    }

    public async Task<Result<SweepReportDto>> RunSweep(string? nowOverride = null, CancellationToken ct = default)
    {
        var query = !string.IsNullOrEmpty(nowOverride) ? $"?now={Uri.EscapeDataString(nowOverride)}" : string.Empty;
        using var req = CreateRequest(HttpMethod.Post, $"{VesselRoutes.Sweep}{query}");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.SweepReportDto, ct);
    }

    public async Task<Result<IReadOnlyList<ServerLogEntryDto>>> GetServerLogs(int limit = 100, string? level = null, string? protocol = null, CancellationToken ct = default)
    {
        var query = $"?limit={limit}";
        if (!string.IsNullOrEmpty(level))
        {
            query += $"&level={Uri.EscapeDataString(level)}";
        }

        if (!string.IsNullOrEmpty(protocol))
        {
            query += $"&protocol={Uri.EscapeDataString(protocol)}";
        }

        using var req = CreateRequest(HttpMethod.Get, $"{VesselRoutes.Logs}{query}");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.IReadOnlyListServerLogEntryDto, ct);
    }

    public async Task<Result> ClearServerLogs(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Delete, VesselRoutes.Logs);
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<IReadOnlyList<string>>> ListContainerRepos(int limit = 100, string? last = null, CancellationToken ct = default)
    {
        var query = $"?n={limit}" + (last is not null ? $"&last={Uri.EscapeDataString(last)}" : "");
        using var req = CreateRequest(HttpMethod.Get, $"{VesselRoutes.OciCatalog}{query}");
        using var res = await http.SendAsync(req, ct);
        var parsed = await ReadJson(res, VesselJsonContext.Default.ContainerCatalogDto, ct);
        return parsed.TryGetValue(out var dto, out var err)
            ? new Result<IReadOnlyList<string>>.Success(dto.Repositories)
            : new Result<IReadOnlyList<string>>.Failure(err);
    }

    public async Task<Result<IReadOnlyList<string>>> ListContainerTags(string repo, int limit = 100, string? last = null, CancellationToken ct = default)
    {
        var query = $"?n={limit}" + (last is not null ? $"&last={Uri.EscapeDataString(last)}" : "");
        using var req = CreateRequest(HttpMethod.Get, $"v2/{Uri.EscapeDataString(repo)}/tags/list{query}");
        using var res = await http.SendAsync(req, ct);
        var parsed = await ReadJson(res, VesselJsonContext.Default.ContainerTagsDto, ct);
        return parsed.TryGetValue(out var dto, out var err)
            ? new Result<IReadOnlyList<string>>.Success(dto.Tags)
            : new Result<IReadOnlyList<string>>.Failure(err);
    }

    public async Task<Result> DeleteContainerManifest(string repo, string reference, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Delete, $"v2/{Uri.EscapeDataString(repo)}/manifests/{Uri.EscapeDataString(reference)}");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<IReadOnlyList<WebhookDto>>> ListWebhooks(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, VesselRoutes.Webhooks);
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.IReadOnlyListWebhookDto, ct);
    }

    public async Task<Result<WebhookDto>> GetWebhook(string id, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, $"{VesselRoutes.Webhooks}/{Uri.EscapeDataString(id)}");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.WebhookDto, ct);
    }

    public async Task<Result<WebhookDto>> CreateWebhook(CreateWebhookDto dto, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Post, VesselRoutes.Webhooks);
        req.Content = CreateJsonContent(dto, VesselJsonContext.Default.CreateWebhookDto);
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.WebhookDto, ct);
    }

    public async Task<Result<WebhookDto>> UpdateWebhook(string id, UpdateWebhookDto dto, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Put, $"{VesselRoutes.Webhooks}/{Uri.EscapeDataString(id)}");
        req.Content = CreateJsonContent(dto, VesselJsonContext.Default.UpdateWebhookDto);
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.WebhookDto, ct);
    }

    public async Task<Result> DeleteWebhook(string id, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Delete, $"{VesselRoutes.Webhooks}/{Uri.EscapeDataString(id)}");
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await ReadError(res, ct);
    }

    public async Task<Result<WebhookTestResultDto>> TestWebhook(string id, CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Post, $"{VesselRoutes.Webhooks}/{Uri.EscapeDataString(id)}/test");
        using var res = await http.SendAsync(req, ct);
        return await ReadJson(res, VesselJsonContext.Default.WebhookTestResultDto, ct);
    }

    public async Task<Result<string>> ExportWebhooksYaml(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, $"{VesselRoutes.Webhooks}/export.yaml");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            return await ReadError(res, ct);
        }
        var yaml = await res.Content.ReadAsStringAsync(ct);
        return yaml;
    }

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

        using var req = CreateRequest(HttpMethod.Get, path);
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(options.BearerToken))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue(VesselHeaders.AuthorizationSchemeBearer, options.BearerToken);
        }
        else if (!string.IsNullOrEmpty(options.AccessKey))
        {
            req.Headers.TryAddWithoutValidation(VesselHeaders.AccessKey, options.AccessKey);
            if (!string.IsNullOrEmpty(options.SecretKey))
            {
                req.Headers.TryAddWithoutValidation(VesselHeaders.SecretKey, options.SecretKey);
            }
        }
        return req;
    }

    private static ByteArrayContent CreateJsonContent<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(VesselMediaTypes.Json) { CharSet = VesselMediaTypes.Utf8CharSet };
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
            if (bytes.Length == 0)
            {
                return new HttpError("HttpError", $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}", (int)res.StatusCode);
            }

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
