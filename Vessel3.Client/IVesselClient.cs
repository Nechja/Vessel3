using Vessel3.Primitives;

namespace Vessel3.Client;

public interface IVesselClient : IDisposable
{
    Task<Result<WhoAmIDto>> WhoAmI(CancellationToken ct = default);

    Task<Result<IReadOnlyList<BucketDto>>> ListBuckets(CancellationToken ct = default);
    Task<Result> CreateBucket(string bucket, CancellationToken ct = default);
    Task<Result> DeleteBucket(string bucket, CancellationToken ct = default);

    Task<Result<BucketAccessDto>> GetBucketAccess(string bucket, CancellationToken ct = default);
    Task<Result> SetBucketAccess(string bucket, BucketAccessDto access, CancellationToken ct = default);

    Task<Result<BucketVersioningDto>> GetBucketVersioning(string bucket, CancellationToken ct = default);
    Task<Result> SetBucketVersioning(string bucket, string status, CancellationToken ct = default);

    Task<Result<BucketWebsiteDto?>> GetBucketWebsite(string bucket, CancellationToken ct = default);
    Task<Result> SetBucketWebsite(string bucket, BucketWebsiteDto website, CancellationToken ct = default);
    Task<Result> DeleteBucketWebsite(string bucket, CancellationToken ct = default);

    Task<Result<ObjectsPageDto>> ListObjects(string bucket, string? prefix = null, string? delimiter = null, string? marker = null, int limit = 1000, CancellationToken ct = default);
    Task<Result<VesselObjectDownload>> GetObject(string bucket, string key, string? versionId = null, CancellationToken ct = default);
    Task<Result<ObjectSummaryDto>> StatObject(string bucket, string key, string? versionId = null, CancellationToken ct = default);
    Task<Result<PutObjectResultDto>> PutObject(string bucket, string key, Stream content, string? contentType = null, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken ct = default);
    Task<Result> DeleteObject(string bucket, string key, string? versionId = null, CancellationToken ct = default);

    Task<Result<IReadOnlyList<UserDto>>> ListUsers(CancellationToken ct = default);
    Task<Result<UserDto>> CreateUser(string username, string role = "Member", CancellationToken ct = default);
    Task<Result> UpdateUserRole(string userId, string role, CancellationToken ct = default);
    Task<Result> UpdateUserStatus(string userId, string status, CancellationToken ct = default);
    Task<Result> DeleteUser(string userId, CancellationToken ct = default);
    Task<Result<AccessKeyDto>> CreateAccessKey(string userId, string? description = null, TimeSpan? ttl = null, CancellationToken ct = default);
    Task<Result<IReadOnlyList<AccessKeyDto>>> ListAccessKeys(string userId, CancellationToken ct = default);
    Task<Result> RevokeAccessKey(string accessKeyId, CancellationToken ct = default);

    Task<Result<GcReportDto>> RunGc(long minBlobAgeSec = 3600, long minUploadAgeSec = 604800, CancellationToken ct = default);
    Task<Result<SweepReportDto>> RunSweep(string? nowOverride = null, CancellationToken ct = default);
    Task<Result<IReadOnlyList<ServerLogEntryDto>>> GetServerLogs(int limit = 100, string? level = null, string? protocol = null, CancellationToken ct = default);
    Task<Result> ClearServerLogs(CancellationToken ct = default);

    Task<Result<IReadOnlyList<string>>> ListContainerRepos(int limit = 100, string? last = null, CancellationToken ct = default);
    Task<Result<IReadOnlyList<string>>> ListContainerTags(string repo, int limit = 100, string? last = null, CancellationToken ct = default);
    Task<Result> DeleteContainerManifest(string repo, string reference, CancellationToken ct = default);

    Task<Result<IReadOnlyList<WebhookDto>>> ListWebhooks(CancellationToken ct = default);
    Task<Result<WebhookDto>> GetWebhook(string id, CancellationToken ct = default);
    Task<Result<WebhookDto>> CreateWebhook(CreateWebhookDto dto, CancellationToken ct = default);
    Task<Result<WebhookDto>> UpdateWebhook(string id, UpdateWebhookDto dto, CancellationToken ct = default);
    Task<Result> DeleteWebhook(string id, CancellationToken ct = default);
    Task<Result<WebhookTestResultDto>> TestWebhook(string id, CancellationToken ct = default);
    Task<Result<string>> ExportWebhooksYaml(CancellationToken ct = default);

    IAsyncEnumerable<VesselEventDto> StreamEvents(string? topicFilter = null, string? resourceFilter = null, CancellationToken ct = default);
}
