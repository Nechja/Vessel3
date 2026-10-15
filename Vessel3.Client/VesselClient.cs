using Vessel3.Primitives;

namespace Vessel3.Client;

public sealed class VesselClient : IVesselClient
{
    public const int DefaultPartSize = ObjectsClient.DefaultPartSize;

    private readonly VesselHttpTransport transport;

    public IBucketsClient Buckets { get; }
    public IObjectsClient Objects { get; }
    public IIdentityClient Identity { get; }
    public IAdminClient Admin { get; }
    public IContainersClient Containers { get; }
    public IWebhooksClient Webhooks { get; }
    public IEventsClient Events { get; }

    public VesselClient(HttpClient http, VesselClientOptions? options = null, bool disposeHttpClient = false)
    {
        transport = new VesselHttpTransport(http, options ?? new VesselClientOptions(), disposeHttpClient);
        Buckets = new BucketsClient(transport);
        Objects = new ObjectsClient(transport);
        Identity = new IdentityClient(transport);
        Admin = new AdminClient(transport);
        Containers = new ContainersClient(transport);
        Webhooks = new WebhooksClient(transport);
        Events = new EventsClient(transport);
    }

    public VesselClient(string baseUrl, string? accessKey = null, string? secretKey = null, string? bearerToken = null)
        : this(
            new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") },
            new VesselClientOptions(baseUrl, accessKey, secretKey, bearerToken),
            disposeHttpClient: true)
    {
    }

    public void Dispose() => transport.Dispose();

    public Task<Result<WhoAmIDto>> WhoAmI(CancellationToken ct = default) =>
        Identity.WhoAmI(ct);

    public Task<Result<IReadOnlyList<UserDto>>> ListUsers(CancellationToken ct = default) =>
        Identity.ListUsers(ct);

    public Task<Result<UserDto>> CreateUser(string username, string role = "Member", CancellationToken ct = default) =>
        Identity.CreateUser(username, role, ct);

    public Task<Result> UpdateUserRole(string userId, string role, CancellationToken ct = default) =>
        Identity.UpdateUserRole(userId, role, ct);

    public Task<Result> UpdateUserStatus(string userId, string status, CancellationToken ct = default) =>
        Identity.UpdateUserStatus(userId, status, ct);

    public Task<Result> DeleteUser(string userId, CancellationToken ct = default) =>
        Identity.DeleteUser(userId, ct);

    public Task<Result<AccessKeyDto>> CreateAccessKey(string userId, string? description = null, TimeSpan? ttl = null, CancellationToken ct = default) =>
        Identity.CreateAccessKey(userId, description, ttl, ct);

    public Task<Result<IReadOnlyList<AccessKeyDto>>> ListAccessKeys(string userId, CancellationToken ct = default) =>
        Identity.ListAccessKeys(userId, ct);

    public Task<Result> RevokeAccessKey(string accessKeyId, CancellationToken ct = default) =>
        Identity.RevokeAccessKey(accessKeyId, ct);

    public Task<Result<IReadOnlyList<BucketDto>>> ListBuckets(CancellationToken ct = default) =>
        Buckets.ListBuckets(ct);

    public Task<Result> CreateBucket(string bucket, CancellationToken ct = default) =>
        Buckets.CreateBucket(bucket, ct);

    public Task<Result> DeleteBucket(string bucket, CancellationToken ct = default) =>
        Buckets.DeleteBucket(bucket, ct);

    public Task<Result<BucketAccessDto>> GetBucketAccess(string bucket, CancellationToken ct = default) =>
        Buckets.GetBucketAccess(bucket, ct);

    public Task<Result> SetBucketAccess(string bucket, BucketAccessDto access, CancellationToken ct = default) =>
        Buckets.SetBucketAccess(bucket, access, ct);

    public Task<Result<BucketVersioningDto>> GetBucketVersioning(string bucket, CancellationToken ct = default) =>
        Buckets.GetBucketVersioning(bucket, ct);

    public Task<Result> SetBucketVersioning(string bucket, string status, CancellationToken ct = default) =>
        Buckets.SetBucketVersioning(bucket, status, ct);

    public Task<Result<BucketWebsiteDto?>> GetBucketWebsite(string bucket, CancellationToken ct = default) =>
        Buckets.GetBucketWebsite(bucket, ct);

    public Task<Result> SetBucketWebsite(string bucket, BucketWebsiteDto website, CancellationToken ct = default) =>
        Buckets.SetBucketWebsite(bucket, website, ct);

    public Task<Result> DeleteBucketWebsite(string bucket, CancellationToken ct = default) =>
        Buckets.DeleteBucketWebsite(bucket, ct);

    public Task<Result<ObjectsPageDto>> ListObjects(string bucket, string? prefix = null, string? delimiter = null, string? marker = null, int limit = 1000, CancellationToken ct = default) =>
        Objects.ListObjects(bucket, prefix, delimiter, marker, limit, ct);

    public Task<Result<VesselObjectDownload>> GetObject(string bucket, string key, string? versionId = null, CancellationToken ct = default) =>
        Objects.GetObject(bucket, key, versionId, ct);

    public Task<Result<ObjectSummaryDto>> StatObject(string bucket, string key, string? versionId = null, CancellationToken ct = default) =>
        Objects.StatObject(bucket, key, versionId, ct);

    public Task<Result<PutObjectResultDto>> PutObject(string bucket, string key, Stream content, string? contentType = null, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken ct = default) =>
        Objects.PutObject(bucket, key, content, contentType, metadata, ct);

    public Task<Result<PutObjectResultDto>> PutObject(string bucket, string key, Stream content, string? contentType, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress, CancellationToken ct = default) =>
        Objects.PutObject(bucket, key, content, contentType, metadata, progress, ct);

    public Task<Result<CreateUploadResultDto>> CreateMultipartUpload(string bucket, string key, string? contentType = null, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken ct = default) =>
        Objects.CreateMultipartUpload(bucket, key, contentType, metadata, ct);

    public Task<Result<UploadPartDto>> UploadPart(string bucket, string key, string uploadId, int partNumber, Stream content, long length, CancellationToken ct = default) =>
        Objects.UploadPart(bucket, key, uploadId, partNumber, content, length, ct);

    public Task<Result<PutObjectResultDto>> CompleteMultipartUpload(string bucket, string key, string uploadId, IReadOnlyList<UploadPartDto> parts, CancellationToken ct = default) =>
        Objects.CompleteMultipartUpload(bucket, key, uploadId, parts, ct);

    public Task<Result> AbortMultipartUpload(string bucket, string key, string uploadId, CancellationToken ct = default) =>
        Objects.AbortMultipartUpload(bucket, key, uploadId, ct);

    public Task<Result<PutObjectResultDto>> UploadObject(string bucket, string key, Stream content, long size, string? contentType = null, IReadOnlyDictionary<string, string>? metadata = null, IProgress<long>? progress = null, CancellationToken ct = default) =>
        Objects.UploadObject(bucket, key, content, size, contentType, metadata, progress, ct);

    public Task<Result> DeleteObject(string bucket, string key, string? versionId = null, CancellationToken ct = default) =>
        Objects.DeleteObject(bucket, key, versionId, ct);

    public Task<Result<GcReportDto>> RunGc(long minBlobAgeSec = 3600, long minUploadAgeSec = 604800, CancellationToken ct = default) =>
        Admin.RunGc(minBlobAgeSec, minUploadAgeSec, ct);

    public Task<Result<SweepReportDto>> RunSweep(string? nowOverride = null, CancellationToken ct = default) =>
        Admin.RunSweep(nowOverride, ct);

    public Task<Result<IReadOnlyList<ServerLogEntryDto>>> GetServerLogs(int limit = 100, string? level = null, string? protocol = null, CancellationToken ct = default) =>
        Admin.GetServerLogs(limit, level, protocol, ct);

    public Task<Result> ClearServerLogs(CancellationToken ct = default) =>
        Admin.ClearServerLogs(ct);

    public Task<Result<IReadOnlyList<string>>> ListContainerRepos(int limit = 100, string? last = null, CancellationToken ct = default) =>
        Containers.ListContainerRepos(limit, last, ct);

    public Task<Result<IReadOnlyList<string>>> ListContainerTags(string repo, int limit = 100, string? last = null, CancellationToken ct = default) =>
        Containers.ListContainerTags(repo, limit, last, ct);

    public Task<Result> DeleteContainerManifest(string repo, string reference, CancellationToken ct = default) =>
        Containers.DeleteContainerManifest(repo, reference, ct);

    public Task<Result<IReadOnlyList<WebhookDto>>> ListWebhooks(CancellationToken ct = default) =>
        Webhooks.ListWebhooks(ct);

    public Task<Result<WebhookDto>> GetWebhook(string id, CancellationToken ct = default) =>
        Webhooks.GetWebhook(id, ct);

    public Task<Result<WebhookDto>> CreateWebhook(CreateWebhookDto dto, CancellationToken ct = default) =>
        Webhooks.CreateWebhook(dto, ct);

    public Task<Result<WebhookDto>> UpdateWebhook(string id, UpdateWebhookDto dto, CancellationToken ct = default) =>
        Webhooks.UpdateWebhook(id, dto, ct);

    public Task<Result> DeleteWebhook(string id, CancellationToken ct = default) =>
        Webhooks.DeleteWebhook(id, ct);

    public Task<Result<WebhookTestResultDto>> TestWebhook(string id, CancellationToken ct = default) =>
        Webhooks.TestWebhook(id, ct);

    public Task<Result<string>> ExportWebhooksYaml(CancellationToken ct = default) =>
        Webhooks.ExportWebhooksYaml(ct);

    public IAsyncEnumerable<VesselEventDto> StreamEvents(string? topicFilter = null, string? resourceFilter = null, CancellationToken ct = default) =>
        Events.StreamEvents(topicFilter, resourceFilter, ct);
}
