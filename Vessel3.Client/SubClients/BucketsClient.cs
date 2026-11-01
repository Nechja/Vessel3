using System.Net;
using Vessel3.Primitives;

namespace Vessel3.Client;

internal sealed class BucketsClient(VesselHttpTransport transport) : IBucketsClient
{
    public async Task<Result<IReadOnlyList<BucketDto>>> ListBuckets(CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Get, VesselRoutes.Buckets);
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.IReadOnlyListBucketDto, ct);
    }

    public async Task<Result> CreateBucket(string bucket, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Put, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}");
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }

    public async Task<Result> DeleteBucket(string bucket, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Delete, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}");
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }

    public async Task<Result<BucketAccessDto>> GetBucketAccess(string bucket, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Get, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/access");
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.BucketAccessDto, ct);
    }

    public async Task<Result> SetBucketAccess(string bucket, BucketAccessDto access, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Put, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/access");
        req.Content = transport.CreateJsonContent(access, VesselJsonContext.Default.BucketAccessDto);
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }

    public async Task<Result<BucketVersioningDto>> GetBucketVersioning(string bucket, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Get, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/versioning");
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.BucketVersioningDto, ct);
    }

    public async Task<Result> SetBucketVersioning(string bucket, string status, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Put, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/versioning");
        req.Content = transport.CreateJsonContent(new BucketVersioningDto(status), VesselJsonContext.Default.BucketVersioningDto);
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }

    public async Task<Result<BucketWebsiteDto?>> GetBucketWebsite(string bucket, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Get, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/website");
        using var res = await transport.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            return (BucketWebsiteDto?)null;
        }
        var readResult = await transport.ReadJson(res, VesselJsonContext.Default.BucketWebsiteDto, ct);
        return readResult.TryGetValue(out var dto, out var err) ? (BucketWebsiteDto?)dto : err;
    }

    public async Task<Result> SetBucketWebsite(string bucket, BucketWebsiteDto website, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Put, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/website");
        req.Content = transport.CreateJsonContent(website, VesselJsonContext.Default.BucketWebsiteDto);
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }

    public async Task<Result> DeleteBucketWebsite(string bucket, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Delete, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/website");
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }

    public async Task<Result<LifecycleConfigDto?>> GetBucketLifecycle(string bucket, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Get, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/lifecycle");
        using var res = await transport.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            return (LifecycleConfigDto?)null;
        }
        var readResult = await transport.ReadJson(res, VesselJsonContext.Default.LifecycleConfigDto, ct);
        return readResult.TryGetValue(out var dto, out var err) ? (LifecycleConfigDto?)dto : err;
    }

    public async Task<Result> SetBucketLifecycle(string bucket, LifecycleConfigDto lifecycle, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Put, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/lifecycle");
        req.Content = transport.CreateJsonContent(lifecycle, VesselJsonContext.Default.LifecycleConfigDto);
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }

    public async Task<Result> DeleteBucketLifecycle(string bucket, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Delete, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/lifecycle");
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }

    public async Task<Result<BucketOwnerDto>> GetBucketOwner(string bucket, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Get, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/owner");
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.BucketOwnerDto, ct);
    }

    public async Task<Result> SetBucketOwner(string bucket, string ownerId, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Put, $"{VesselRoutes.Buckets}/{Uri.EscapeDataString(bucket)}/owner");
        req.Content = transport.CreateJsonContent(new BucketOwnerDto(ownerId), VesselJsonContext.Default.BucketOwnerDto);
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }
}
