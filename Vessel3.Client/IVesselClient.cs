using Vessel3.Primitives;

namespace Vessel3.Client;

public interface IVesselClient : IDisposable
{
    Task<Result<WhoAmIDto>> WhoAmIAsync(CancellationToken ct = default);

    Task<Result<IReadOnlyList<BucketDto>>> ListBucketsAsync(CancellationToken ct = default);
    Task<Result> CreateBucketAsync(string bucket, CancellationToken ct = default);
    Task<Result> DeleteBucketAsync(string bucket, CancellationToken ct = default);

    Task<Result<BucketAccessDto>> GetBucketAccessAsync(string bucket, CancellationToken ct = default);
    Task<Result> SetBucketAccessAsync(string bucket, BucketAccessDto access, CancellationToken ct = default);

    Task<Result<BucketVersioningDto>> GetBucketVersioningAsync(string bucket, CancellationToken ct = default);
    Task<Result> SetBucketVersioningAsync(string bucket, string status, CancellationToken ct = default);

    Task<Result<ObjectsPageDto>> ListObjectsAsync(string bucket, string? prefix = null, string? delimiter = null, string? marker = null, int limit = 1000, CancellationToken ct = default);
    Task<Result<VesselObjectDownload>> GetObjectAsync(string bucket, string key, string? versionId = null, CancellationToken ct = default);
    Task<Result<ObjectSummaryDto>> StatObjectAsync(string bucket, string key, string? versionId = null, CancellationToken ct = default);
    Task<Result<PutObjectResultDto>> PutObjectAsync(string bucket, string key, Stream content, string? contentType = null, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken ct = default);
    Task<Result> DeleteObjectAsync(string bucket, string key, string? versionId = null, CancellationToken ct = default);

    Task<Result<IReadOnlyList<UserDto>>> ListUsersAsync(CancellationToken ct = default);
    Task<Result<UserDto>> CreateUserAsync(string username, string role = "Member", CancellationToken ct = default);
    Task<Result> UpdateUserRoleAsync(string userId, string role, CancellationToken ct = default);
    Task<Result> UpdateUserStatusAsync(string userId, string status, CancellationToken ct = default);
    Task<Result> DeleteUserAsync(string userId, CancellationToken ct = default);
    Task<Result<AccessKeyDto>> CreateAccessKeyAsync(string userId, string? description = null, TimeSpan? ttl = null, CancellationToken ct = default);
    Task<Result<IReadOnlyList<AccessKeyDto>>> ListAccessKeysAsync(string userId, CancellationToken ct = default);
    Task<Result> RevokeAccessKeyAsync(string accessKeyId, CancellationToken ct = default);

    Task<Result<GcReportDto>> RunGcAsync(long minBlobAgeSec = 3600, long minUploadAgeSec = 604800, CancellationToken ct = default);
    Task<Result<SweepReportDto>> RunSweepAsync(string? nowOverride = null, CancellationToken ct = default);

    Task<Result<IReadOnlyList<string>>> ListContainerReposAsync(int limit = 100, string? last = null, CancellationToken ct = default);
    Task<Result<IReadOnlyList<string>>> ListContainerTagsAsync(string repo, int limit = 100, string? last = null, CancellationToken ct = default);
    Task<Result> DeleteContainerManifestAsync(string repo, string reference, CancellationToken ct = default);
}
