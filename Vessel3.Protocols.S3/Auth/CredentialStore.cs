using System.Collections.Concurrent;
using System.Security.Cryptography;
using Vessel3.Primitives;
using Vessel3.Storage;

namespace Vessel3.Server.S3;

internal sealed record Credential(
    string AccessKey,
    string Secret,
    string? SessionToken,
    DateTimeOffset? ExpiresAt,
    string? Subject = null,
    string? AccountId = null,
    CallerIdentity? Caller = null);

internal interface ICredentialStore
{
    Credential? Find(string accessKey);
    Credential IssueSession(string subject, TimeSpan ttl, string? accountId = null);
}

internal sealed class CredentialStore(Credential? root, IIdentityRegistry? identity, TimeProvider clock) : ICredentialStore
{
    private const string SessionKeyPrefix = "ASIA";
    private const string KeyAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private readonly ConcurrentDictionary<string, Credential> sessions = new(StringComparer.Ordinal);

    public CredentialStore(Credential? root, TimeProvider clock) : this(root, null, clock) { }

    public Credential? Find(string accessKey)
    {
        if (root is not null && string.Equals(accessKey, root.AccessKey, StringComparison.Ordinal))
            return root;

        if (sessions.TryGetValue(accessKey, out var session))
            return session;

        if (identity is null)
        {
            return null;
        }

        if (identity.GetAccessKey(accessKey) is not Result<AccessKey?>.Success { Value: { } key }
            || key.IsRevoked
            || (key.ExpiresAt is not null && key.ExpiresAt <= clock.GetUtcNow()))
        {
            return null;
        }

        if (identity.GetUser(key.UserId) is not Result<User?>.Success { Value: { } user }
            || user.Status is not UserStatus.Active)
        {
            return null;
        }

        var caller = new CallerIdentity(user.Id, user.Username, user.Role, key.Id);
        return new Credential(key.Id, key.SecretKey, null, key.ExpiresAt, user.Username, null, caller);
    }

    public Credential IssueSession(string subject, TimeSpan ttl, string? accountId = null)
    {
        var now = clock.GetUtcNow();
        Sweep(now);
        while (true)
        {
            var ak = SessionKeyPrefix + RandomNumberGenerator.GetString(KeyAlphabet, 16);
            var caller = new CallerIdentity(subject, subject, UserRole.Member, ak);
            var cred = new Credential(
                ak,
                RandomToken(30),
                RandomToken(32),
                now + ttl,
                subject,
                accountId,
                caller);
            if (sessions.TryAdd(cred.AccessKey, cred)) return cred;
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var kv in sessions)
        {
            if (kv.Value.ExpiresAt is { } exp && exp <= now) sessions.TryRemove(kv.Key, out _);
        }
    }

    private static string RandomToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));
}
