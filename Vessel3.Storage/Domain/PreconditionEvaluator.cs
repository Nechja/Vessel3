using System.Globalization;

namespace Vessel3.Storage;

internal enum Precondition
{
    Pass,
    NotModified,
    Failed,
}

internal interface IPreconditionEvaluator
{
    Precondition Evaluate(PreconditionRules rules, string etag, DateTimeOffset lastModified);
    Precondition EvaluateForWrite(WritePreconditions rules, string? currentEtag);
    bool HasWriteConditions(WritePreconditions rules);
}

internal sealed class PreconditionEvaluator : IPreconditionEvaluator
{
    private static readonly string[] DateFormats =
    [
        "r",
        "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
        "dddd, dd-MMM-yy HH:mm:ss 'GMT'",
        "ddd MMM d HH:mm:ss yyyy",
        "ddd, d MMM yyyy HH:mm:ss 'GMT'",
        "dd MMM yyyy HH:mm:ss 'GMT'",
    ];

    public Precondition Evaluate(PreconditionRules rules, string etag, DateTimeOffset lastModified)
    {
        if (rules is { IfMatch: null, IfNoneMatch: null, IfModifiedSince: null, IfUnmodifiedSince: null })
            return Precondition.Pass;

        var lastModSec = TruncateToSecond(lastModified);

        if (IsMatchFailed(rules.IfMatch, etag) || IsUnmodifiedFailed(rules.IfUnmodifiedSince, lastModSec))
            return Precondition.Failed;

        return IsNoneMatchHit(rules.IfNoneMatch, etag) || IsModifiedSinceHit(rules.IfNoneMatch, rules.IfModifiedSince, lastModSec)
            ? Precondition.NotModified
            : Precondition.Pass;
    }

    public Precondition EvaluateForWrite(WritePreconditions rules, string? currentEtag) =>
        rules is { IfMatch: null, IfNoneMatch: null }
            ? Precondition.Pass
            : IsWriteMatchFailed(rules.IfMatch, currentEtag) || IsWriteNoneMatchFailed(rules.IfNoneMatch, currentEtag)
                ? Precondition.Failed
                : Precondition.Pass;

    public bool HasWriteConditions(WritePreconditions rules) =>
        !string.IsNullOrEmpty(rules.IfMatch) || !string.IsNullOrEmpty(rules.IfNoneMatch);

    private static bool IsMatchFailed(string? ifMatch, string etag) =>
        !string.IsNullOrEmpty(ifMatch)
        && ifMatch is not "*"
        && !EtagListContains(ifMatch, etag);

    private static bool IsUnmodifiedFailed(string? ifUnmodSince, DateTimeOffset lastModSec) =>
        !string.IsNullOrEmpty(ifUnmodSince)
        && TryParseHttpDate(ifUnmodSince, out var unmodSince)
        && lastModSec > unmodSince;

    private static bool IsNoneMatchHit(string? ifNoneMatch, string etag) =>
        !string.IsNullOrEmpty(ifNoneMatch)
        && (ifNoneMatch is "*" || EtagListContains(ifNoneMatch, etag));

    private static bool IsModifiedSinceHit(string? ifNoneMatch, string? ifModSince, DateTimeOffset lastModSec) =>
        string.IsNullOrEmpty(ifNoneMatch)
        && !string.IsNullOrEmpty(ifModSince)
        && TryParseHttpDate(ifModSince, out var modSince)
        && lastModSec <= modSince;

    private static bool IsWriteMatchFailed(string? ifMatch, string? currentEtag) =>
        !string.IsNullOrEmpty(ifMatch)
        && ifMatch is not "*"
        && (currentEtag is null || !EtagListContains(ifMatch, currentEtag));

    private static bool IsWriteNoneMatchFailed(string? ifNoneMatch, string? currentEtag) =>
        currentEtag is not null
        && !string.IsNullOrEmpty(ifNoneMatch)
        && (ifNoneMatch is "*" || EtagListContains(ifNoneMatch, currentEtag));

    private static bool EtagListContains(string headerValue, string etag)
    {
        ReadOnlySpan<char> span = headerValue.AsSpan();
        ReadOnlySpan<char> target = etag.AsSpan();
        while (!span.IsEmpty)
        {
            var comma = span.IndexOf(',');
            var item = (comma >= 0 ? span[..comma] : span).Trim();
            if (item.StartsWith("W/", StringComparison.Ordinal))
            {
                item = item[2..];
            }

            if (item.Length >= 2 && item[0] is '"' && item[^1] is '"')
            {
                item = item[1..^1];
            }

            if (item.SequenceEqual(target))
            {
                return true;
            }

            if (comma < 0)
            {
                break;
            }

            span = span[(comma + 1)..];
        }

        return false;
    }

    private static bool TryParseHttpDate(string s, out DateTimeOffset dt) =>
        DateTimeOffset.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt);

    private static DateTimeOffset TruncateToSecond(DateTimeOffset value)
    {
        var ticks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerSecond);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
