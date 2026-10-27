using System.Text.Json.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native;

public readonly record struct BucketDto(string Name, DateTimeOffset CreatedAt, string? OwnerId);

public readonly record struct BucketAccessDto(bool PublicRead, bool ReadOnly);

public readonly record struct BucketVersioningDto(string Status);

public readonly record struct BucketWebsiteDto(string IndexDocument, string? ErrorDocument = null);

public readonly record struct ObjectSummaryDto(string Key, long Size, string ETag, DateTimeOffset LastModified, string? VersionId);

public readonly record struct ObjectsPageDto(IReadOnlyList<ObjectSummaryDto> Objects, IReadOnlyList<string> Prefixes, bool IsTruncated, string? NextMarker);

public readonly record struct PutObjectResultDto(string ETag, string? VersionId, long Size, string? Sha256);

public readonly record struct CreateUploadResultDto(string UploadId, string Bucket, string Key);

public readonly record struct UploadPartDto(int PartNumber, string ETag);

public readonly record struct CompleteUploadRequestDto(IReadOnlyList<UploadPartDto> Parts);

public readonly record struct UserDto(string Id, string Username, string Role, string Status, DateTimeOffset CreatedAt);

public readonly record struct CreateUserRequest(string Username, string Role);

public readonly record struct UpdateUserRoleRequest(string Role);

public readonly record struct UpdateUserStatusRequest(string Status);

public readonly record struct AccessKeyDto(string Id, string SecretKey, string UserId, string? Description, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool IsRevoked);

public readonly record struct CreateAccessKeyRequest(string? Description, long? TtlSeconds);

public readonly record struct WhoAmIDto(string UserId, string Username, string Role, string? AccessKeyId);

public readonly record struct ErrorDto(string Error, string Message);

public readonly record struct GcReportDto(int BlobsDeleted, int UploadsReaped);

public readonly record struct SweepReportDto(int Expired, int MarkersReaped);

public readonly record struct WebhookDto(
    string Id,
    string Name,
    string Url,
    string? Secret,
    IReadOnlyList<string> EventFilters,
    IReadOnlyList<string>? ResourceFilters,
    bool Active,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastTriggeredAt,
    int? LastStatusCode,
    string? LastError,
    bool IsStatic);

public readonly record struct CreateWebhookDto(
    string Name,
    string Url,
    string? Secret,
    IReadOnlyList<string> EventFilters,
    IReadOnlyList<string>? ResourceFilters,
    bool Active);

public readonly record struct UpdateWebhookDto(
    string Name,
    string Url,
    string? Secret,
    IReadOnlyList<string> EventFilters,
    IReadOnlyList<string>? ResourceFilters,
    bool Active);

public readonly record struct WebhookTestResultDto(
    string WebhookId,
    bool Success,
    int? StatusCode,
    double LatencyMs,
    string? ErrorMessage,
    string? ResponseBody);

public readonly record struct ServerLogEntryDto(
    string Id,
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message,
    string? Protocol,
    string? Action,
    string? Subject,
    string? Actor,
    int? StatusCode,
    double? DurationMs,
    string? TraceId,
    string? ErrorDetails);

public readonly record struct AdvertisedVolumeDto(
    string VolumeId,
    string Pool,
    IReadOnlyList<string> Capabilities,
    string Status,
    long? CapacityBytes = null,
    long? AvailableBytes = null);

public readonly record struct ClusterPeerDto(
    string NodeId,
    string Endpoint,
    long Incarnation,
    string Status,
    DateTimeOffset LastSeenUtc,
    IReadOnlyList<AdvertisedVolumeDto> Volumes);

public readonly record struct VolumeDto(
    string Id,
    string Path,
    string Pool,
    IReadOnlyList<string> Capabilities,
    string Status,
    bool IsRemote,
    bool IsWritable,
    bool IsReadable,
    bool IsDefaultIngest,
    long? CapacityBytes = null,
    long? AvailableBytes = null,
    string? NodeId = null);

public readonly record struct ClusterStatusDto(
    string NodeId,
    string Region,
    bool ClusterEnabled,
    ClusterPeerDto LocalNode,
    IReadOnlyList<ClusterPeerDto> Peers,
    IReadOnlyList<VolumeDto> Volumes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BucketDto))]
[JsonSerializable(typeof(IReadOnlyList<BucketDto>))]
[JsonSerializable(typeof(List<BucketDto>))]
[JsonSerializable(typeof(BucketAccessDto))]
[JsonSerializable(typeof(BucketVersioningDto))]
[JsonSerializable(typeof(BucketWebsiteDto))]
[JsonSerializable(typeof(ObjectSummaryDto))]
[JsonSerializable(typeof(ObjectsPageDto))]
[JsonSerializable(typeof(PutObjectResultDto))]
[JsonSerializable(typeof(CreateUploadResultDto))]
[JsonSerializable(typeof(UploadPartDto))]
[JsonSerializable(typeof(IReadOnlyList<UploadPartDto>))]
[JsonSerializable(typeof(List<UploadPartDto>))]
[JsonSerializable(typeof(CompleteUploadRequestDto))]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(IReadOnlyList<UserDto>))]
[JsonSerializable(typeof(List<UserDto>))]
[JsonSerializable(typeof(CreateUserRequest))]
[JsonSerializable(typeof(UpdateUserRoleRequest))]
[JsonSerializable(typeof(UpdateUserStatusRequest))]
[JsonSerializable(typeof(AccessKeyDto))]
[JsonSerializable(typeof(IReadOnlyList<AccessKeyDto>))]
[JsonSerializable(typeof(List<AccessKeyDto>))]
[JsonSerializable(typeof(CreateAccessKeyRequest))]
[JsonSerializable(typeof(WhoAmIDto))]
[JsonSerializable(typeof(ErrorDto))]
[JsonSerializable(typeof(GcReportDto))]
[JsonSerializable(typeof(SweepReportDto))]
[JsonSerializable(typeof(WebhookDto))]
[JsonSerializable(typeof(IReadOnlyList<WebhookDto>))]
[JsonSerializable(typeof(List<WebhookDto>))]
[JsonSerializable(typeof(CreateWebhookDto))]
[JsonSerializable(typeof(UpdateWebhookDto))]
[JsonSerializable(typeof(WebhookTestResultDto))]
[JsonSerializable(typeof(VesselEvent))]
[JsonSerializable(typeof(IReadOnlyList<VesselEvent>))]
[JsonSerializable(typeof(ServerLogEntryDto))]
[JsonSerializable(typeof(IReadOnlyList<ServerLogEntryDto>))]
[JsonSerializable(typeof(List<ServerLogEntryDto>))]
[JsonSerializable(typeof(AdvertisedVolumeDto))]
[JsonSerializable(typeof(IReadOnlyList<AdvertisedVolumeDto>))]
[JsonSerializable(typeof(List<AdvertisedVolumeDto>))]
[JsonSerializable(typeof(ClusterPeerDto))]
[JsonSerializable(typeof(IReadOnlyList<ClusterPeerDto>))]
[JsonSerializable(typeof(List<ClusterPeerDto>))]
[JsonSerializable(typeof(VolumeDto))]
[JsonSerializable(typeof(IReadOnlyList<VolumeDto>))]
[JsonSerializable(typeof(List<VolumeDto>))]
[JsonSerializable(typeof(ClusterStatusDto))]
internal partial class NativeJsonContext : JsonSerializerContext;
