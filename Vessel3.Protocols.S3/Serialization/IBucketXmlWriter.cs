using Vessel3.Storage;

namespace Vessel3.Server.S3;

internal interface IBucketXmlWriter
{
    Task WriteListBuckets(Stream output, IEnumerable<BucketInfo> buckets, CancellationToken ct);
    Task WriteLocationConstraint(Stream output, string region, CancellationToken ct);
    Task WriteVersioningConfiguration(Stream output, VersioningStatus status, CancellationToken ct);
    Task WriteWebsiteConfiguration(Stream output, WebsiteConfig cfg, CancellationToken ct);
    Task WriteCorsConfiguration(Stream output, CorsConfig cfg, CancellationToken ct);
    Task WriteAccessControlPolicy(Stream output, string ownerId, bool publicRead, CancellationToken ct);
    Task WriteObjectLockConfiguration(Stream output, ObjectLockConfig cfg, CancellationToken ct);
    Task WriteLifecycleConfiguration(Stream output, LifecycleConfig cfg, CancellationToken ct);
    Task WriteListMultipartUploads(Stream output, string bucket, IEnumerable<InProgressUpload> uploads, CancellationToken ct);
}
