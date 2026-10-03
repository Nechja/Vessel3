using Vessel3.Primitives;
using Vessel3.Storage;
using Xunit;

namespace Vessel3.Tests;

public class IdentityRegistryTests : IDisposable
{
    private readonly string root;
    private readonly TestClock clock = new(DateTimeOffset.UtcNow);
    private readonly List<IDisposable> disposables = [];

    public IdentityRegistryTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"vessel3-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        foreach (var d in disposables) d.Dispose();
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private IdentityRegistry CreateRegistry()
    {
        var reg = new IdentityRegistry(new IdentityOptions(root), clock);
        disposables.Add(reg);
        return reg;
    }

    [Fact]
    public void CreateUser_ValidInput_Persists()
    {
        var reg = CreateRegistry();

        var emptyResult = reg.CreateUser("");
        Assert.True(emptyResult.Match(_ => false, err => err is InvalidArgumentError));

        var createResult = reg.CreateUser("alice", UserRole.Admin);
        Assert.True(createResult.TryGetValue(out var user, out _));
        Assert.Equal("alice", user.Username);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.StartsWith("usr_", user.Id, StringComparison.Ordinal);

        var duplicateResult = reg.CreateUser("alice");
        Assert.True(duplicateResult.Match(_ => false, err => err is InvalidArgumentError));

        var getResult = reg.GetUser(user.Id);
        Assert.True(getResult.TryGetValue(out var fetched, out _));
        Assert.NotNull(fetched);
        Assert.Equal(user.Id, fetched.Id);
        Assert.Equal("alice", fetched.Username);

        var findResult = reg.FindUserByUsername("alice");
        Assert.True(findResult.TryGetValue(out var found, out _));
        Assert.NotNull(found);
        Assert.Equal(user.Id, found.Id);

        var missing = reg.FindUserByUsername("nonexistent");
        Assert.True(missing.TryGetValue(out var notFound, out _));
        Assert.Null(notFound);
    }

    [Fact]
    public void ListUsers_Default_ReturnsSortedUsers()
    {
        var reg = CreateRegistry();
        reg.CreateUser("charlie");
        reg.CreateUser("bob");
        reg.CreateUser("alice");

        var listResult = reg.ListUsers();
        Assert.True(listResult.TryGetValue(out var users, out _));
        Assert.Equal(3, users.Count);
        Assert.Equal("alice", users[0].Username);
        Assert.Equal("bob", users[1].Username);
        Assert.Equal("charlie", users[2].Username);
    }

    [Fact]
    public void UpdateUser_RoleAndStatus_Persists()
    {
        var reg = CreateRegistry();
        var user = ((Result<User>.Success)reg.CreateUser("bob", UserRole.Member)).Value;

        var updateRoleResult = reg.UpdateUserRole(user.Id, UserRole.Admin);
        Assert.Same(Result.Ok, updateRoleResult);

        var updateStatusResult = reg.UpdateUserStatus(user.Id, UserStatus.Suspended);
        Assert.Same(Result.Ok, updateStatusResult);

        var refreshed = ((Result<User?>.Success)reg.GetUser(user.Id)).Value;
        Assert.NotNull(refreshed);
        Assert.Equal(UserRole.Admin, refreshed.Role);
        Assert.Equal(UserStatus.Suspended, refreshed.Status);

        var badRole = reg.UpdateUserRole("nonexistent", UserRole.Admin);
        Assert.True(badRole.Match(() => false, err => err is NotFoundError));

        var badStatus = reg.UpdateUserStatus("nonexistent", UserStatus.Active);
        Assert.True(badStatus.Match(() => false, err => err is NotFoundError));
    }

    [Fact]
    public void DeleteUser_ExistingUser_CascadesToAccessKeys()
    {
        var reg = CreateRegistry();
        var user = ((Result<User>.Success)reg.CreateUser("david")).Value;
        var key = ((Result<AccessKey>.Success)reg.CreateAccessKey(user.Id)).Value;

        var deleteResult = reg.DeleteUser(user.Id);
        Assert.Same(Result.Ok, deleteResult);

        var fetchUser = ((Result<User?>.Success)reg.GetUser(user.Id)).Value;
        Assert.Null(fetchUser);

        var fetchKey = ((Result<AccessKey?>.Success)reg.GetAccessKey(key.Id)).Value;
        Assert.Null(fetchKey);

        var deleteMissing = reg.DeleteUser("nonexistent");
        Assert.True(deleteMissing.Match(() => false, err => err is NotFoundError));
    }

    [Fact]
    public void AccessKeys_Lifecycle_CreatesListsAndRevokes()
    {
        var reg = CreateRegistry();
        var user = ((Result<User>.Success)reg.CreateUser("eva")).Value;

        var keyResult = reg.CreateAccessKey(user.Id, "test key", TimeSpan.FromDays(30));
        Assert.True(keyResult.TryGetValue(out var key, out _));
        Assert.StartsWith("V3AK", key.Id, StringComparison.Ordinal);
        Assert.Equal(20, key.Id.Length);
        Assert.Equal(40, key.SecretKey.Length);
        Assert.Equal("test key", key.Description);
        Assert.False(key.IsRevoked);
        Assert.NotNull(key.ExpiresAt);

        var badUserKey = reg.CreateAccessKey("nonexistent");
        Assert.True(badUserKey.Match(_ => false, err => err is NotFoundError));

        var listResult = reg.ListAccessKeys(user.Id);
        Assert.True(listResult.TryGetValue(out var keys, out _));
        Assert.Single(keys);
        Assert.Equal(key.Id, keys[0].Id);

        var revokeResult = reg.RevokeAccessKey(key.Id);
        Assert.Same(Result.Ok, revokeResult);

        var revokedKey = ((Result<AccessKey?>.Success)reg.GetAccessKey(key.Id)).Value;
        Assert.NotNull(revokedKey);
        Assert.True(revokedKey.IsRevoked);

        var revokeMissing = reg.RevokeAccessKey("V3AKNOTFOUND");
        Assert.True(revokeMissing.Match(() => false, err => err is NotFoundError));
    }

    [Fact]
    public void AuthenticateAccessKey_StatusAndExpiration_Verifies()
    {
        var reg = CreateRegistry();
        var user = ((Result<User>.Success)reg.CreateUser("frank", UserRole.Member)).Value;
        var key = ((Result<AccessKey>.Success)reg.CreateAccessKey(user.Id, ttl: TimeSpan.FromMinutes(10))).Value;

        var authSuccess = reg.AuthenticateAccessKey(key.Id);
        Assert.True(authSuccess.TryGetValue(out var caller, out _));
        Assert.Equal(user.Id, caller.UserId);
        Assert.Equal("frank", caller.Username);
        Assert.Equal(UserRole.Member, caller.Role);
        Assert.Equal(key.Id, caller.AccessKeyId);

        var unknownKey = reg.AuthenticateAccessKey("V3AKUNKNOWNKEY000000");
        Assert.True(unknownKey.Match(_ => false, err => err is InvalidAccessKeyIdError));

        clock.Now += TimeSpan.FromMinutes(11);
        var expiredAuth = reg.AuthenticateAccessKey(key.Id);
        Assert.True(expiredAuth.Match(_ => false, err => err is ExpiredTokenError));

        var freshKey = ((Result<AccessKey>.Success)reg.CreateAccessKey(user.Id)).Value;
        reg.RevokeAccessKey(freshKey.Id);
        var revokedAuth = reg.AuthenticateAccessKey(freshKey.Id);
        Assert.True(revokedAuth.Match(_ => false, err => err is AccessDeniedError));

        var suspendedKey = ((Result<AccessKey>.Success)reg.CreateAccessKey(user.Id)).Value;
        reg.UpdateUserStatus(user.Id, UserStatus.Suspended);
        var suspendedAuth = reg.AuthenticateAccessKey(suspendedKey.Id);
        Assert.True(suspendedAuth.Match(_ => false, err => err is AccessDeniedError));
    }

    [Fact]
    public void EnsureBootstrapAdmin_MultipleInvocations_IsIdempotentAndAuthenticatable()
    {
        var reg = CreateRegistry();
        var bootstrapResult = reg.EnsureBootstrapAdmin("rootaccesskey12345", "rootsecretkey1234567890abcdef");
        Assert.Same(Result.Ok, bootstrapResult);

        var rebootstrap = reg.EnsureBootstrapAdmin("rootaccesskey12345", "rootsecretkey1234567890abcdef");
        Assert.Same(Result.Ok, rebootstrap);

        var callerResult = reg.AuthenticateAccessKey("rootaccesskey12345");
        Assert.True(callerResult.TryGetValue(out var caller, out _));
        Assert.Equal("admin", caller.Username);
        Assert.Equal(UserRole.Admin, caller.Role);

        var adminUser = ((Result<User?>.Success)reg.FindUserByUsername("admin")).Value;
        Assert.NotNull(adminUser);
        Assert.Equal(UserRole.Admin, adminUser.Role);
    }

    [Fact]
    public void Data_MultipleInstances_PersistsAcrossInstances()
    {
        var reg1 = CreateRegistry();
        var user = ((Result<User>.Success)reg1.CreateUser("grace", UserRole.Admin)).Value;
        var key = ((Result<AccessKey>.Success)reg1.CreateAccessKey(user.Id)).Value;

        using var reg2 = new IdentityRegistry(new IdentityOptions(root), clock);
        var fetchedUser = ((Result<User?>.Success)reg2.GetUser(user.Id)).Value;
        Assert.NotNull(fetchedUser);
        Assert.Equal("grace", fetchedUser.Username);
        Assert.Equal(UserRole.Admin, fetchedUser.Role);

        var authResult = reg2.AuthenticateAccessKey(key.Id);
        Assert.True(authResult.TryGetValue(out var caller, out _));
        Assert.Equal("grace", caller.Username);
    }

    [Fact]
    public void EnsureAdminUsers_MatchingUsers_PromotesCaseInsensitive()
    {
        var reg = CreateRegistry();
        var u1 = ((Result<User>.Success)reg.CreateUser("acct_kayla.dIftEd_eU48bcFmhcaiAJA", UserRole.Member)).Value;
        var u2 = ((Result<User>.Success)reg.CreateUser("bob_standard", UserRole.Member)).Value;
        var u3 = ((Result<User>.Success)reg.CreateUser("super_ALICE_user", UserRole.ReadOnly)).Value;

        var res = reg.EnsureAdminUsers(["kayla", "alice"]);
        Assert.Same(Result.Ok, res);

        var refreshedU1 = ((Result<User?>.Success)reg.GetUser(u1.Id)).Value!;
        var refreshedU2 = ((Result<User?>.Success)reg.GetUser(u2.Id)).Value!;
        var refreshedU3 = ((Result<User?>.Success)reg.GetUser(u3.Id)).Value!;

        Assert.Equal(UserRole.Admin, refreshedU1.Role);
        Assert.Equal(UserRole.Member, refreshedU2.Role);
        Assert.Equal(UserRole.Admin, refreshedU3.Role);
    }
}
