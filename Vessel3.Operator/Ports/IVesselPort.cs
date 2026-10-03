using Vessel3.Primitives;
using Vessel3.Operator.Domain.Models;

namespace Vessel3.Operator.Ports;

public interface IVesselPort : IDisposable
{
    Task<Result> EnsureBucket(string bucket, CancellationToken ct = default);
    Task<Result> ConfigureVersioning(string bucket, string status, CancellationToken ct = default);
    Task<Result> ConfigureWebsite(string bucket, BucketWebsiteDefinition website, CancellationToken ct = default);
    Task<Result> ConfigureAccess(string bucket, string access, CancellationToken ct = default);
    Task<Result<BucketStatsSummary>> FetchBucketStats(string bucket, CancellationToken ct = default);
    Task<Result> DeleteBucket(string bucket, CancellationToken ct = default);

    Task<Result> EnsureUser(string username, string role, CancellationToken ct = default);
    Task<Result<UserAccessKey>> IssueAccessKey(string username, string? description = null, CancellationToken ct = default);
}
