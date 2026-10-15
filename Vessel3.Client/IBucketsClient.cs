using Vessel3.Primitives;

namespace Vessel3.Client;

public interface IBucketsClient
{
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
}
