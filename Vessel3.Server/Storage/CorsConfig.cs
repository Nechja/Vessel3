using System.Text.Json.Serialization;

namespace Vessel3.Server.Storage;

internal sealed record CorsRule(
    IReadOnlyList<string> AllowedOrigins,
    IReadOnlyList<string> AllowedMethods,
    IReadOnlyList<string>? AllowedHeaders = null,
    IReadOnlyList<string>? ExposeHeaders = null,
    int? MaxAgeSeconds = null,
    string? Id = null)
{
    public bool MatchesOrigin(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;
        foreach (var allowed in AllowedOrigins)
        {
            if (WildcardMatch(allowed, origin)) return true;
        }
        return false;
    }

    public bool MatchesMethod(string method)
    {
        if (string.IsNullOrWhiteSpace(method)) return false;
        foreach (var m in AllowedMethods)
        {
            if (string.Equals(m, method, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public bool MatchesHeaders(IEnumerable<string> requestHeaders)
    {
        if (AllowedHeaders is null or { Count: 0 }) return true;
        if (AllowedHeaders.Contains("*")) return true;

        foreach (var reqH in requestHeaders)
        {
            var trimmed = reqH.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            var matched = false;
            foreach (var allowed in AllowedHeaders)
            {
                if (WildcardMatch(allowed, trimmed))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched) return false;
        }
        return true;
    }

    public static bool WildcardMatch(string pattern, string value)
    {
        if (pattern == "*") return true;
        var starIdx = pattern.IndexOf('*');
        if (starIdx < 0) return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);

        var prefix = pattern.AsSpan(0, starIdx);
        if (!value.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var suffix = pattern.AsSpan(starIdx + 1);
        if (suffix.IsEmpty) return true;

        var nextStar = suffix.IndexOf('*');
        if (nextStar < 0)
        {
            return value.Length >= pattern.Length - 1
                && value.AsSpan().EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        var valSpan = value.AsSpan(starIdx);
        var patSpan = suffix;
        while (!patSpan.IsEmpty)
        {
            var idx = patSpan.IndexOf('*');
            var part = idx >= 0 ? patSpan[..idx] : patSpan;
            if (part.IsEmpty)
            {
                patSpan = patSpan[1..];
                continue;
            }

            if (idx < 0)
            {
                return valSpan.EndsWith(part, StringComparison.OrdinalIgnoreCase);
            }

            var found = valSpan.IndexOf(part, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            valSpan = valSpan[(found + part.Length)..];
            patSpan = patSpan[(idx + 1)..];
        }
        return true;
    }
}

internal sealed record CorsConfig(IReadOnlyList<CorsRule> Rules)
{
    public CorsRule? FindMatch(string origin, string method)
    {
        foreach (var rule in Rules)
        {
            if (rule.MatchesOrigin(origin) && rule.MatchesMethod(method))
                return rule;
        }
        return null;
    }

    public CorsRule? FindPreflightMatch(string origin, string method, IEnumerable<string> requestHeaders)
    {
        foreach (var rule in Rules)
        {
            if (rule.MatchesOrigin(origin) && rule.MatchesMethod(method) && rule.MatchesHeaders(requestHeaders))
                return rule;
        }
        return null;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = false)]
[JsonSerializable(typeof(CorsConfig))]
[JsonSerializable(typeof(CorsRule))]
[JsonSerializable(typeof(List<CorsRule>))]
internal sealed partial class CorsJsonContext : JsonSerializerContext;
