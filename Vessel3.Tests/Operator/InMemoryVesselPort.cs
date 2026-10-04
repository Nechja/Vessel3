using Vessel3.Client;
using Vessel3.Operator.Domain.Models;
using Vessel3.Operator.Ports;

namespace Vessel3.Tests.Operator;

public sealed class InMemoryVesselPort : IVesselPort
{
    public HashSet<string> Buckets { get; } = [];
    public Dictionary<string, string> BucketVersioning { get; } = [];
    public Dictionary<string, BucketWebsiteDefinition> BucketWebsites { get; } = [];
    public Dictionary<string, string> BucketAccesses { get; } = [];
    public Dictionary<string, BucketStatsSummary> BucketStats { get; } = [];
    public Dictionary<string, string> Users { get; } = [];
    public List<UserAccessKey> IssuedKeys { get; } = [];
    public List<WebhookDto> Webhooks { get; } = [];

    public bool ShouldFailEnsureBucket { get; set; }
    public bool ShouldFailEnsureUser { get; set; }
    public bool ShouldFailIssueKey { get; set; }
    public bool ShouldFailEnsureWebhook { get; set; }
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

    public Task<Result> ConfigureWebsite(string bucket, BucketWebsiteDefinition website, CancellationToken ct = default)
    {
        BucketWebsites[bucket] = website;
        return Task.FromResult(Result.Ok);
    }

    public Task<Result> ConfigureAccess(string bucket, string access, CancellationToken ct = default)
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

    public Task<Result<IReadOnlyList<WebhookDto>>> ListWebhooks(CancellationToken ct = default) =>
        Task.FromResult<Result<IReadOnlyList<WebhookDto>>>(Webhooks);

    public Task<Result<WebhookDto>> EnsureWebhook(CreateWebhookDto dto, CancellationToken ct = default)
    {
        if (ShouldFailEnsureWebhook)
        {
            return Task.FromResult<Result<WebhookDto>>(new Error("WebhookError", "Failed to ensure webhook"));
        }

        var existing = Webhooks.FirstOrDefault(w => string.Equals(w.Name, dto.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return Task.FromResult<Result<WebhookDto>>(existing);
        }

        var hook = new WebhookDto(
            Id: Guid.NewGuid().ToString("N"),
            Name: dto.Name,
            Url: dto.Url,
            Secret: dto.Secret,
            EventFilters: dto.EventFilters,
            ResourceFilters: dto.ResourceFilters,
            Active: dto.Active,
            CreatedAt: DateTimeOffset.UtcNow,
            LastTriggeredAt: null,
            LastStatusCode: null,
            LastError: null,
            IsStatic: false);

        Webhooks.Add(hook);
        return Task.FromResult<Result<WebhookDto>>(hook);
    }

    public Task<Result<WebhookDto>> UpdateWebhook(string id, UpdateWebhookDto dto, CancellationToken ct = default)
    {
        var index = Webhooks.FindIndex(w => w.Id == id);
        if (index < 0)
        {
            return Task.FromResult<Result<WebhookDto>>(new Error("NotFound", "Webhook not found"));
        }

        var updated = Webhooks[index] with
        {
            Name = dto.Name,
            Url = dto.Url,
            Secret = dto.Secret,
            EventFilters = dto.EventFilters,
            ResourceFilters = dto.ResourceFilters,
            Active = dto.Active
        };

        Webhooks[index] = updated;
        return Task.FromResult<Result<WebhookDto>>(updated);
    }

    public Task<Result> DeleteWebhook(string id, CancellationToken ct = default)
    {
        Webhooks.RemoveAll(w => w.Id == id);
        return Task.FromResult(Result.Ok);
    }

    public void Dispose()
    {
        Disposed = true;
    }
}
