using Vessel3.Primitives;

namespace Vessel3.Client;

public interface IIdentityClient
{
    Task<Result<WhoAmIDto>> WhoAmI(CancellationToken ct = default);
    Task<Result<IReadOnlyList<UserDto>>> ListUsers(CancellationToken ct = default);
    Task<Result<UserDto>> CreateUser(string username, string role = "Member", CancellationToken ct = default);
    Task<Result> UpdateUserRole(string userId, string role, CancellationToken ct = default);
    Task<Result> UpdateUserStatus(string userId, string status, CancellationToken ct = default);
    Task<Result> DeleteUser(string userId, CancellationToken ct = default);
    Task<Result<AccessKeyDto>> CreateAccessKey(string userId, string? description = null, TimeSpan? ttl = null, CancellationToken ct = default);
    Task<Result<IReadOnlyList<AccessKeyDto>>> ListAccessKeys(string userId, CancellationToken ct = default);
    Task<Result> RevokeAccessKey(string accessKeyId, CancellationToken ct = default);
}
