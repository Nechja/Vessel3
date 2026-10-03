using Vessel3.Client;
using Vessel3.Primitives;
using Vessel3.Operator.Ports;

namespace Vessel3.Tests.Operator;

public sealed class InMemoryVesselPort : IVesselPort
{
    public HashSet<string> Buckets { get; } = [];
    public Dictionary<string, string> BucketVersioning { get; } = [];
    public Dictionary<string, BucketWebsiteDto> BucketWebsites { get; } = [];
    public Dictionary<string, BucketAccessDto> BucketAccesses { get; } = [];
    public Dictionary<string, BucketStatsSummary> BucketStats { get; } = [];
    public Dictionary<string, string> Users { get; } = [];
    public List<UserAccessKey> IssuedKeys { get; } = [];

    public bool ShouldFailEnsureBucket { get; set; }
    public bool ShouldFailEnsureUser { get; set; }
    public bool ShouldFailIssueKey { get; set; }
    public bool Disposed { get; private set; }

    public Task<Result> EnsureBucket(string bucket, CancellationToken ct = default)
    {
        if (ShouldFailEnsureBucket)
        {
            return Task.FromResult<Result>(new Error("BucketError", "Failed to ensure bucket"));
        }

        Buckets.Add(bucket);
        return Task.FromResult(Result.Ok);
    }

    public Task<Result> ConfigureVersioning(string bucket, string status, CancellationToken ct = default)
    {
        BucketVersioning[bucket] = status;
        return Task.FromResult(Result.Ok);
    }

    public Task<Result> ConfigureWebsite(string bucket, BucketWebsiteDto website, CancellationToken ct = default)
    {
        BucketWebsites[bucket] = website;
        return Task.FromResult(Result.Ok);
    }

    public Task<Result> ConfigureAccess(string bucket, BucketAccessDto access, CancellationToken ct = default)
    {
        BucketAccesses[bucket] = access;
        return Task.FromResult(Result.Ok);
    }

    public Task<Result<BucketStatsSummary>> FetchBucketStats(string bucket, CancellationToken ct = default)
    {
        if (BucketStats.TryGetValue(bucket, out var stats))
        {
            return Task.FromResult<Result<BucketStatsSummary>>(stats);
        }

        return Task.FromResult<Result<BucketStatsSummary>>(new BucketStatsSummary(1024L, 5L));
    }

    public Task<Result> DeleteBucket(string bucket, CancellationToken ct = default)
    {
        Buckets.Remove(bucket);
        return Task.FromResult(Result.Ok);
    }

    public Task<Result> EnsureUser(string username, string role, CancellationToken ct = default)
    {
        if (ShouldFailEnsureUser)
        {
            return Task.FromResult<Result>(new Error("UserError", "Failed to ensure user"));
        }

        Users[username] = role;
        return Task.FromResult(Result.Ok);
    }

    public Task<Result<UserAccessKey>> IssueAccessKey(string username, string? description = null, CancellationToken ct = default)
    {
        if (ShouldFailIssueKey)
        {
            return Task.FromResult<Result<UserAccessKey>>(new Error("KeyError", "Failed to issue access key"));
        }

        var key = new UserAccessKey("test-key-id", "test-secret-key");
        IssuedKeys.Add(key);
        return Task.FromResult<Result<UserAccessKey>>(key);
    }

    public void Dispose()
    {
        Disposed = true;
    }
}
