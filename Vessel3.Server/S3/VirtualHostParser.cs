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
        if (IsIpAddress(host)) return false;

        // Check against configured base domains
        foreach (var domain in baseDomains)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase))
                return false;

            // "admin.<domain>" is reserved for the UI
            if (host.Equals($"admin.{domain}", StringComparison.OrdinalIgnoreCase))
                return false;

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

        // Custom domain: if host directly matches an existing bucket name
        if (registry.IsValidName(host) && registry.Exists(host) is Result<bool>.Success { Value: true })
        {
            bucket = host.ToLowerInvariant();
            return true;
        }

        return false;
    }

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
