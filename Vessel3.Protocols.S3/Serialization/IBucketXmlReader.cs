using Vessel3.Storage;

namespace Vessel3.Server.S3;

internal interface IBucketXmlReader
{
    Task<Result<VersioningStatus>> ReadVersioningConfiguration(Stream input, CancellationToken ct);
    Task<Result<ObjectLockConfig>> ReadObjectLockConfiguration(Stream input, CancellationToken ct);
    Task<Result<LifecycleConfig>> ReadLifecycleConfiguration(Stream input, CancellationToken ct);
    Task<Result<WebsiteConfig>> ReadWebsiteConfiguration(Stream input, CancellationToken ct);
    Task<Result<CorsConfig>> ReadCorsConfiguration(Stream input, CancellationToken ct);
    Task<Result<bool>> ReadAccessControlPolicy(Stream input, CancellationToken ct);
}
