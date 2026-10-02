using System.Text.Json.Serialization;

namespace Vessel3.Client;

public readonly record struct BucketDto(string Name, DateTimeOffset CreatedAt, string? OwnerId);

public readonly record struct BucketAccessDto(bool PublicRead, bool ReadOnly);

public readonly record struct BucketVersioningDto(string Status);

public readonly record struct ObjectSummaryDto(string Key, long Size, string ETag, DateTimeOffset LastModified, string? VersionId);

public readonly record struct ObjectsPageDto(IReadOnlyList<ObjectSummaryDto> Objects, IReadOnlyList<string> Prefixes, bool IsTruncated, string? NextMarker);

public readonly record struct PutObjectResultDto(string ETag, string? VersionId, long Size, string? Sha256);

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

public readonly record struct ContainerCatalogDto(IReadOnlyList<string> Repositories);

public readonly record struct ContainerTagsDto(string Name, IReadOnlyList<string> Tags);

public sealed record VesselObjectDownload(
    Stream Content,
    string ContentType,
    long ContentLength,
    string ETag,
    DateTimeOffset? LastModified,
    string? VersionId,
    IReadOnlyDictionary<string, string> Metadata,
    HttpResponseMessage HttpResponse) : IDisposable
{
    public void Dispose()
    {
        Content.Dispose();
        HttpResponse.Dispose();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BucketDto))]
[JsonSerializable(typeof(IReadOnlyList<BucketDto>))]
[JsonSerializable(typeof(BucketAccessDto))]
[JsonSerializable(typeof(BucketVersioningDto))]
[JsonSerializable(typeof(ObjectSummaryDto))]
[JsonSerializable(typeof(ObjectsPageDto))]
[JsonSerializable(typeof(PutObjectResultDto))]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(IReadOnlyList<UserDto>))]
[JsonSerializable(typeof(CreateUserRequest))]
[JsonSerializable(typeof(UpdateUserRoleRequest))]
[JsonSerializable(typeof(UpdateUserStatusRequest))]
[JsonSerializable(typeof(AccessKeyDto))]
[JsonSerializable(typeof(IReadOnlyList<AccessKeyDto>))]
[JsonSerializable(typeof(CreateAccessKeyRequest))]
[JsonSerializable(typeof(WhoAmIDto))]
[JsonSerializable(typeof(ErrorDto))]
[JsonSerializable(typeof(GcReportDto))]
[JsonSerializable(typeof(SweepReportDto))]
[JsonSerializable(typeof(ContainerCatalogDto))]
[JsonSerializable(typeof(ContainerTagsDto))]
internal partial class VesselJsonContext : JsonSerializerContext;
