namespace Vessel3.Storage;

internal interface IIdentityRegistry : IDisposable
{
    Result<User> CreateUser(string username, UserRole role = UserRole.Member);
    Result<User?> GetUser(string userId);
    Result<User?> FindUserByUsername(string username);
    Result<IReadOnlyList<User>> ListUsers();
    Result UpdateUserRole(string userId, UserRole role);
    Result UpdateUserStatus(string userId, UserStatus status);
    Result DeleteUser(string userId);

    Result<AccessKey> CreateAccessKey(string userId, string? description = null, TimeSpan? ttl = null);
    Result<AccessKey?> GetAccessKey(string accessKeyId);
    Result<IReadOnlyList<AccessKey>> ListAccessKeys(string userId);
    Result RevokeAccessKey(string accessKeyId);

    Result<CallerIdentity> AuthenticateAccessKey(string accessKeyId);
    Result EnsureBootstrapAdmin(string? accessKey, string? secretKey);
    Result EnsureAdminUsers(IReadOnlyList<string> adminPatterns);
}
