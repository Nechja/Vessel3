using Vessel3.Client;
using Vessel3.Operator.Domain.Models;
using Vessel3.Operator.Ports;
using Vessel3.Primitives;

namespace Vessel3.Operator.Adapters.Vessel;

public sealed class VesselHttpAdapter(IVesselClient client) : IVesselPort
{
    public async Task<Result> EnsureBucket(string bucket, CancellationToken ct = default)
    {
        var existingResult = await client.ListBuckets(ct);
        var exists = existingResult.TryGetValue(out var buckets, out _)
            && buckets.Any(b => string.Equals(b.Name, bucket, StringComparison.OrdinalIgnoreCase));

        return exists ? Result.Ok : await client.CreateBucket(bucket, ct);
    }

    public Task<Result> ConfigureVersioning(string bucket, string status, CancellationToken ct = default) =>
        client.SetBucketVersioning(bucket, status, ct);

    public Task<Result> ConfigureWebsite(string bucket, BucketWebsiteDefinition website, CancellationToken ct = default) =>
        client.SetBucketWebsite(bucket, new BucketWebsiteDto(website.IndexDocument, website.ErrorDocument), ct);

    public async Task<Result> ConfigureAccess(string bucket, string access, CancellationToken ct = default)
    {
        var publicRead = string.Equals(access, "public-read", StringComparison.OrdinalIgnoreCase)
            || string.Equals(access, "public", StringComparison.OrdinalIgnoreCase);
        var readOnly = string.Equals(access, "readonly", StringComparison.OrdinalIgnoreCase);
        var dto = new BucketAccessDto(publicRead, readOnly);
        return await client.SetBucketAccess(bucket, dto, ct);
    }

    public async Task<Result<BucketStatsSummary>> FetchBucketStats(string bucket, CancellationToken ct = default)
    {
        var result = await client.ListObjects(bucket, limit: 1000, ct: ct);
        if (!result.TryGetValue(out var page, out var err))
        {
            return err;
        }

        var totalSize = page.Objects.Sum(o => o.Size);
        var totalCount = (long)page.Objects.Count;
        return new BucketStatsSummary(totalSize, totalCount);
    }

    public Task<Result> DeleteBucket(string bucket, CancellationToken ct = default) =>
        client.DeleteBucket(bucket, ct);

    public async Task<Result> EnsureUser(string username, string role, CancellationToken ct = default)
    {
        var usersResult = await client.ListUsers(ct);
        var exists = usersResult.TryGetValue(out var users, out _)
            && users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

        if (exists)
        {
            return Result.Ok;
        }

        var createResult = await client.CreateUser(username, role, ct);
        return createResult.TryGetValue(out _, out var err) ? Result.Ok : err;
    }

    public async Task<Result<UserAccessKey>> IssueAccessKey(string username, string? description = null, CancellationToken ct = default)
    {
        var usersResult = await client.ListUsers(ct);
        if (!usersResult.TryGetValue(out var users, out var listErr))
        {
            return listErr;
        }

        var found = false;
        UserDto user = default;
        foreach (var u in users)
        {
            if (string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase))
            {
                user = u;
                found = true;
                break;
            }
        }

        if (!found)
        {
            return new Error("NotFound", $"User {username} not found");
        }

        var existingKeysResult = await client.ListAccessKeys(user.Id, ct);
        if (existingKeysResult.TryGetValue(out var existingKeys, out _) && existingKeys.Count > 0)
        {
            var activeKey = existingKeys[0];
            if (!string.IsNullOrEmpty(activeKey.SecretKey))
            {
                return new UserAccessKey(activeKey.Id, activeKey.SecretKey);
            }
        }

        var createKeyResult = await client.CreateAccessKey(user.Id, description, ct: ct);
        return createKeyResult.TryGetValue(out var newKey, out var keyErr)
            ? new UserAccessKey(newKey.Id, newKey.SecretKey ?? string.Empty)
            : keyErr;
    }

    public Task<Result<IReadOnlyList<WebhookDto>>> ListWebhooks(CancellationToken ct = default) =>
        client.ListWebhooks(ct);

    public async Task<Result<WebhookDto>> EnsureWebhook(CreateWebhookDto dto, CancellationToken ct = default)
    {
        var listResult = await client.ListWebhooks(ct);
        if (listResult.TryGetValue(out var hooks, out _))
        {
            var existing = hooks.FirstOrDefault(h => string.Equals(h.Name, dto.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }
        }

        return await client.CreateWebhook(dto, ct);
    }

    public Task<Result<WebhookDto>> UpdateWebhook(string id, UpdateWebhookDto dto, CancellationToken ct = default) =>
        client.UpdateWebhook(id, dto, ct);

    public Task<Result> DeleteWebhook(string id, CancellationToken ct = default) =>
        client.DeleteWebhook(id, ct);

    public void Dispose() => client.Dispose();
}
