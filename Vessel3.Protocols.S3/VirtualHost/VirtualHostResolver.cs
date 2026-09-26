using System.Collections.Frozen;
using System.Net;

namespace Vessel3.Server.S3;

internal interface IVirtualHostResolver
{
    bool IsAdminHost(string? hostHeader);
    bool TryExtractBucket(string? hostHeader, out string bucket);
}

internal sealed class VirtualHostResolver(IBucketRegistry registry, VirtualHostOptions options) : IVirtualHostResolver
{
    private readonly record struct DomainEntry(string Domain, string Suffix);

    private readonly FrozenSet<string> adminDomains = options.BaseDomains
        .Select(d => $"admin.{d}")
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly DomainEntry[] baseDomainEntries = [.. options.BaseDomains.Select(d => new DomainEntry(d, "." + d))];

    public bool IsAdminHost(string? hostHeader) =>
        !string.IsNullOrWhiteSpace(hostHeader) && adminDomains.Contains(StripPort(hostHeader));

    public bool TryExtractBucket(string? hostHeader, out string bucket)
    {
        bucket = string.Empty;
        if (string.IsNullOrWhiteSpace(hostHeader))
        {
            return false;
        }

        var host = StripPort(hostHeader).ToLowerInvariant();
        return !IsIpAddress(host)
            && (TryExtractSubdomainBucket(host, out bucket) || TryExtractCustomDomainBucket(host, out bucket));
    }

    private bool TryExtractSubdomainBucket(string host, out string bucket)
    {
        bucket = string.Empty;
        foreach (ref readonly var entry in baseDomainEntries.AsSpan())
        {
            if (host.Equals(entry.Domain, StringComparison.OrdinalIgnoreCase) || adminDomains.Contains(host))
            {
                continue;
            }

            if (!host.EndsWith(entry.Suffix, StringComparison.OrdinalIgnoreCase) || host.Length <= entry.Suffix.Length)
            {
                continue;
            }

            var candidate = host[..^entry.Suffix.Length];
            if (registry.IsValidName(candidate))
            {
                bucket = candidate.ToLowerInvariant();
                return true;
            }
        }

        return false;
    }

    private bool TryExtractCustomDomainBucket(string host, out string bucket)
    {
        bucket = string.Empty;
        if (!registry.IsValidName(host) || registry.Exists(host) is not Result<bool>.Success { Value: true })
        {
            return false;
        }

        bucket = host.ToLowerInvariant();
        return true;
    }

    public static string StripPort(string host)
    {
        if (host.StartsWith('['))
        {
            var closingBracket = host.IndexOf(']');
            if (closingBracket < 0)
            {
                return host;
            }

            var bracketColon = host.IndexOf(':', closingBracket);
            return bracketColon >= 0 ? host[..bracketColon] : host;
        }

        var colon = host.IndexOf(':');
        return colon >= 0 ? host[..colon] : host;
    }

    private static bool IsIpAddress(string host) =>
        IPAddress.TryParse(host.AsSpan(), out _)
        || (host.Length >= 2 && host[0] == '[' && host[^1] == ']' && IPAddress.TryParse(host.AsSpan(1, host.Length - 2), out _));
}
