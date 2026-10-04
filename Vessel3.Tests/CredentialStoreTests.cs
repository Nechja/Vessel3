using Xunit;

namespace Vessel3.Tests;

public class CredentialStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Credential Root = new("AKIAROOT", "rootsecret", null, null);

    [Fact]
    public void Find_RootKey_ReturnsRootCredential()
    {
        var store = new CredentialStore(Root, new TestClock(T0));
        Assert.Same(Root, store.Find("AKIAROOT"));
    }

    [Fact]
    public void Find_UnknownKey_ReturnsNull()
    {
        var store = new CredentialStore(Root, new TestClock(T0));
        Assert.Null(store.Find("AKIANOPE"));
    }

    [Fact]
    public void Find_NoRootConfigured_ReturnsNull()
    {
        var store = new CredentialStore(null, new TestClock(T0));
        Assert.Null(store.Find("AKIAROOT"));
    }

    [Fact]
    public void IssueSession_ValidInput_ReturnsFindableCredential()
    {
        var store = new CredentialStore(Root, new TestClock(T0));
        var session = store.IssueSession("acct_kayla", TimeSpan.FromHours(1));

        Assert.StartsWith("ASIA", session.AccessKey, StringComparison.Ordinal);
        Assert.Equal(20, session.AccessKey.Length);
        Assert.NotNull(session.SessionToken);
        Assert.Equal(T0 + TimeSpan.FromHours(1), session.ExpiresAt);
        Assert.Same(session, store.Find(session.AccessKey));
    }

    [Fact]
    public void IssueSession_RepeatedCalls_ProducesDistinctCredentials()
    {
        var store = new CredentialStore(Root, new TestClock(T0));
        var a = store.IssueSession("acct_kayla", TimeSpan.FromHours(1));
        var b = store.IssueSession("acct_kayla", TimeSpan.FromHours(1));

        Assert.NotEqual(a.AccessKey, b.AccessKey);
        Assert.NotEqual(a.Secret, b.Secret);
        Assert.NotEqual(a.SessionToken, b.SessionToken);
    }

    [Fact]
    public void IssueSession_ExpiredSessions_SweepsOnNextIssue()
    {
        var clock = new TestClock(T0);
        var store = new CredentialStore(Root, clock);
        var old = store.IssueSession("acct_kayla", TimeSpan.FromMinutes(5));

        clock.Now = T0 + TimeSpan.FromMinutes(5);
        store.IssueSession("acct_kayla", TimeSpan.FromMinutes(5));

        Assert.Null(store.Find(old.AccessKey));
    }

    [Fact]
    public void Find_ExpiredSession_StaysFindableUntilSwept()
    {
        var clock = new TestClock(T0);
        var store = new CredentialStore(Root, clock);
        var session = store.IssueSession("acct_kayla", TimeSpan.FromMinutes(5));

        clock.Now = T0 + TimeSpan.FromHours(1);

        Assert.Same(session, store.Find(session.AccessKey));
    }

    [Fact]
    public void Find_IdentityRegistry_ResolvesCredentials()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"vessel3-cs-{Guid.NewGuid():N}");
        try
        {
            var clock = new TestClock(T0);
            using var identity = new IdentityRegistry(new IdentityOptions(tempDir), clock);
            var user = ((Result<User>.Success)identity.CreateUser("alice", UserRole.Member)).Value;
            var key = ((Result<AccessKey>.Success)identity.CreateAccessKey(user.Id)).Value;

            var store = new CredentialStore(Root, identity, clock);
            var found = store.Find(key.Id);

            Assert.NotNull(found);
            Assert.Equal(key.Id, found.AccessKey);
            Assert.Equal(key.SecretKey, found.Secret);
            Assert.NotNull(found.Caller);
            Assert.Equal("alice", found.Caller.Username);
            Assert.Equal(UserRole.Member, found.Caller.Role);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Find_RevokedKey_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"vessel3-cs-{Guid.NewGuid():N}");
        try
        {
            var clock = new TestClock(T0);
            using var identity = new IdentityRegistry(new IdentityOptions(tempDir), clock);
            var user = ((Result<User>.Success)identity.CreateUser("bob")).Value;
            var key = ((Result<AccessKey>.Success)identity.CreateAccessKey(user.Id)).Value;
            identity.RevokeAccessKey(key.Id);

            var store = new CredentialStore(Root, identity, clock);
            Assert.Null(store.Find(key.Id));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Find_ExpiredKey_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"vessel3-cs-{Guid.NewGuid():N}");
        try
        {
            var clock = new TestClock(T0);
            using var identity = new IdentityRegistry(new IdentityOptions(tempDir), clock);
            var user = ((Result<User>.Success)identity.CreateUser("charlie")).Value;
            var key = ((Result<AccessKey>.Success)identity.CreateAccessKey(user.Id, ttl: TimeSpan.FromMinutes(10))).Value;

            var store = new CredentialStore(Root, identity, clock);
            Assert.NotNull(store.Find(key.Id));

            clock.Now += TimeSpan.FromMinutes(11);
            Assert.Null(store.Find(key.Id));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Find_SuspendedUser_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"vessel3-cs-{Guid.NewGuid():N}");
        try
        {
            var clock = new TestClock(T0);
            using var identity = new IdentityRegistry(new IdentityOptions(tempDir), clock);
            var user = ((Result<User>.Success)identity.CreateUser("david")).Value;
            var key = ((Result<AccessKey>.Success)identity.CreateAccessKey(user.Id)).Value;

            var store = new CredentialStore(Root, identity, clock);
            Assert.NotNull(store.Find(key.Id));

            identity.UpdateUserStatus(user.Id, UserStatus.Suspended);
            Assert.Null(store.Find(key.Id));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
