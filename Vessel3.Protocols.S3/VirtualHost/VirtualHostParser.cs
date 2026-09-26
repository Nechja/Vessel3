using System.Net;

namespace Vessel3.Server.S3;

internal static class VirtualHostParser
{
    public static bool IsAdminHost(string? hostHeader, IReadOnlyList<string> baseDomains)
    {
        if (string.IsNullOrWhiteSpace(hostHeader)) return false;
        var host = StripPort(hostHeader);
        foreach (var domain in baseDomains)
        {
            if (host.Equals($"admin.{domain}", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool TryExtractBucket(string? hostHeader, IReadOnlyList<string> baseDomains, IBucketRegistry registry, out string bucket)
    {
        bucket = string.Empty;
        if (string.IsNullOrWhiteSpace(hostHeader)) return false;

        var host = StripPort(hostHeader).ToLowerInvariant();
        return !IsIpAddress(host)
            && (TryExtractSubdomainBucket(host, baseDomains, registry, out bucket)
                || TryExtractCustomDomainBucket(host, registry, out bucket));
    }

    private static bool TryExtractSubdomainBucket(string host, IReadOnlyList<string> baseDomains, IBucketRegistry registry, out string bucket)
    {
        bucket = string.Empty;
        foreach (var domain in baseDomains)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) || IsReservedAdminDomain(host, domain))
                continue;

            var suffix = "." + domain;
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length)
            {
                var candidate = host[..^suffix.Length];
                if (registry.IsValidName(candidate))
                {
                    bucket = candidate.ToLowerInvariant();
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryExtractCustomDomainBucket(string host, IBucketRegistry registry, out string bucket)
    {
        bucket = string.Empty;
        if (registry.IsValidName(host) && registry.Exists(host) is Result<bool>.Success { Value: true })
        {
            bucket = host.ToLowerInvariant();
            return true;
        }
        return false;
    }

    private static bool IsReservedAdminDomain(string host, string domain) =>
        host.Equals($"admin.{domain}", StringComparison.OrdinalIgnoreCase);

    public static string StripPort(string host)
    {
        if (host.StartsWith('['))
        {
            var closingBracket = host.IndexOf(']');
            if (closingBracket >= 0)
            {
                var colon = host.IndexOf(':', closingBracket);
                return colon >= 0 ? host[..colon] : host;
            }
        }
        var c = host.IndexOf(':');
        return c >= 0 ? host[..c] : host;
    }

    private static bool IsIpAddress(string host) =>
        IPAddress.TryParse(host, out _)
        || (host.StartsWith('[') && host.EndsWith(']') && IPAddress.TryParse(host[1..^1], out _));
}
