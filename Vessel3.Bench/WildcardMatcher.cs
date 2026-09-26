namespace Vessel3.Bench;

internal readonly record struct WildcardTestCase(string Pattern, string Value, bool ExpectedResult);

internal static class WildcardMatcher
{
    public static bool Match(string pattern, string value)
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

internal static class WildcardTestCases
{
    public static readonly WildcardTestCase[] All =
    [
        new("*", "https://anywhere.org", true),
        new("https://dashboard.example.com", "https://dashboard.example.com", true),
        new("https://dashboard.example.com", "https://portal.example.com", false),
        new("https://*.example.com", "https://api.example.com", true),
        new("https://*.example.com", "https://eu-west-1.api.example.com", true),
        new("https://*.example.com", "http://api.example.com", false),
        new("https://*.example.com", "https://api.example.org", false),
        new("https://*.*.example.com", "https://prod.us-east.example.com", true),
        new("https://*.*.example.com", "https://example.com", false),
        new("x-amz-meta-*", "x-amz-meta-custom-uuid-header-field", true),
        new("x-amz-meta-*", "x-amz-date", false),
        new("*.service.internal", "storage.service.internal", true),
    ];
}
