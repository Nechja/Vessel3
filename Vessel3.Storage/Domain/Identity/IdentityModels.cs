namespace Vessel3.Storage;

internal enum UserRole
{
    Admin = 0,
    Member = 1,
    ReadOnly = 2,
}

internal enum UserStatus
{
    Active = 0,
    Suspended = 1,
}

internal sealed record User(
    string Id,
    string Username,
    UserRole Role,
    UserStatus Status,
    DateTimeOffset CreatedAt);

internal sealed record AccessKey(
    string Id,
    string SecretKey,
    string UserId,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool IsRevoked);

internal sealed record CallerIdentity(
    string UserId,
    string Username,
    UserRole Role,
    string AccessKeyId)
{
    public bool IsAdmin => Role == UserRole.Admin;
    public bool CanWrite => Role != UserRole.ReadOnly;
}
