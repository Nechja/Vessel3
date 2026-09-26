using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Vessel3.Server.S3;

internal sealed record Credential(string AccessKey, string Secret, string? SessionToken, DateTimeOffset? ExpiresAt, string? Subject = null, string? AccountId = null);

internal interface ICredentialStore
{
    Credential? Find(string accessKey);
    Credential IssueSession(string subject, TimeSpan ttl, string? accountId = null);
}

internal sealed class CredentialStore(Credential? root, TimeProvider clock) : ICredentialStore
{
    private const string SessionKeyPrefix = "ASIA";
    private const string KeyAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private readonly ConcurrentDictionary<string, Credential> sessions = new(StringComparer.Ordinal);

    public Credential? Find(string accessKey)
    {
        return root is not null && accessKey == root.AccessKey ? root
            : sessions.TryGetValue(accessKey, out var session) ? session
            : null;
    }

    public Credential IssueSession(string subject, TimeSpan ttl, string? accountId = null)
    {
        var now = clock.GetUtcNow();
        Sweep(now);
        while (true)
        {
            var cred = new Credential(
                SessionKeyPrefix + RandomNumberGenerator.GetString(KeyAlphabet, 16),
                RandomToken(30),
                RandomToken(32),
                now + ttl,
                subject,
                accountId);
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
