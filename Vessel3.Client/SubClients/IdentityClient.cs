using Vessel3.Primitives;

namespace Vessel3.Client;

internal sealed class IdentityClient(VesselHttpTransport transport) : IIdentityClient
{
    public async Task<Result<WhoAmIDto>> WhoAmI(CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Get, VesselRoutes.WhoAmI);
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.WhoAmIDto, ct);
    }

    public async Task<Result<IReadOnlyList<UserDto>>> ListUsers(CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Get, VesselRoutes.Users);
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.IReadOnlyListUserDto, ct);
    }

    public async Task<Result<UserDto>> CreateUser(string username, string role = "Member", CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Post, VesselRoutes.Users);
        req.Content = transport.CreateJsonContent(new CreateUserRequest(username, role), VesselJsonContext.Default.CreateUserRequest);
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.UserDto, ct);
    }

    public async Task<Result> UpdateUserRole(string userId, string role, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Put, $"{VesselRoutes.Users}/{Uri.EscapeDataString(userId)}/role");
        req.Content = transport.CreateJsonContent(new UpdateUserRoleRequest(role), VesselJsonContext.Default.UpdateUserRoleRequest);
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }

    public async Task<Result> UpdateUserStatus(string userId, string status, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Put, $"{VesselRoutes.Users}/{Uri.EscapeDataString(userId)}/status");
        req.Content = transport.CreateJsonContent(new UpdateUserStatusRequest(status), VesselJsonContext.Default.UpdateUserStatusRequest);
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }

    public async Task<Result> DeleteUser(string userId, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Delete, $"{VesselRoutes.Users}/{Uri.EscapeDataString(userId)}");
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }

    public async Task<Result<AccessKeyDto>> CreateAccessKey(string userId, string? description = null, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Post, $"{VesselRoutes.Users}/{Uri.EscapeDataString(userId)}/keys");
        var ttlSec = ttl.HasValue ? (long)ttl.Value.TotalSeconds : (long?)null;
        req.Content = transport.CreateJsonContent(new CreateAccessKeyRequest(description, ttlSec), VesselJsonContext.Default.CreateAccessKeyRequest);
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.AccessKeyDto, ct);
    }

    public async Task<Result<IReadOnlyList<AccessKeyDto>>> ListAccessKeys(string userId, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Get, $"{VesselRoutes.Users}/{Uri.EscapeDataString(userId)}/keys");
        using var res = await transport.SendAsync(req, ct);
        return await transport.ReadJson(res, VesselJsonContext.Default.IReadOnlyListAccessKeyDto, ct);
    }

    public async Task<Result> RevokeAccessKey(string accessKeyId, CancellationToken ct = default)
    {
        using var req = transport.CreateRequest(HttpMethod.Delete, $"{VesselRoutes.Keys}/{Uri.EscapeDataString(accessKeyId)}");
        using var res = await transport.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? Result.Ok : await transport.ReadError(res, ct);
    }
}
