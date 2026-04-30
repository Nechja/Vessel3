using System.Globalization;
using Microsoft.Net.Http.Headers;

namespace Vessel3.Server;

internal enum Precondition
{
    Pass,
    NotModified,
    Failed,
}

internal interface IPreconditionEvaluator
{
    Precondition EvaluateForRead(IHeaderDictionary headers, string etag, DateTimeOffset lastModified);
    Precondition EvaluateForWrite(IHeaderDictionary headers, string? currentEtag);
    bool HasWriteConditions(IHeaderDictionary headers);
}

internal sealed class PreconditionEvaluator : IPreconditionEvaluator
{
    public Precondition EvaluateForRead(IHeaderDictionary headers, string etag, DateTimeOffset lastModified)
    {
        var quoted = $"\"{etag}\"";
        var ifMatch = headers["If-Match"].ToString();
        var ifNoneMatch = headers["If-None-Match"].ToString();
        var ifModSince = headers["If-Modified-Since"].ToString();
        var ifUnmodSince = headers["If-Unmodified-Since"].ToString();
        var lastModSec = TruncateToSecond(lastModified);

        if (!string.IsNullOrEmpty(ifMatch) && ifMatch is not "*"
            && !ifMatch.Contains(quoted, StringComparison.Ordinal))
            return Precondition.Failed;

        if (!string.IsNullOrEmpty(ifUnmodSince)
            && TryParseHttpDate(ifUnmodSince, out var unmodSince)
            && lastModSec > unmodSince)
            return Precondition.Failed;

        var noneMatchHit = !string.IsNullOrEmpty(ifNoneMatch)
            && (ifNoneMatch is "*" || ifNoneMatch.Contains(quoted, StringComparison.Ordinal));
        var modSinceHit = string.IsNullOrEmpty(ifNoneMatch)
            && !string.IsNullOrEmpty(ifModSince)
            && TryParseHttpDate(ifModSince, out var modSince)
            && lastModSec <= modSince;

        return noneMatchHit || modSinceHit ? Precondition.NotModified : Precondition.Pass;
    }

    public Precondition EvaluateForWrite(IHeaderDictionary headers, string? currentEtag)
    {
        var ifMatch = headers["If-Match"].ToString();
        var ifNoneMatch = headers["If-None-Match"].ToString();
        var quoted = currentEtag is not null ? $"\"{currentEtag}\"" : null;

        if (!string.IsNullOrEmpty(ifMatch) && ifMatch is not "*")
        {
            if (quoted is null) return Precondition.Failed;
            if (!ifMatch.Contains(quoted, StringComparison.Ordinal)) return Precondition.Failed;
        }

        if (!string.IsNullOrEmpty(ifNoneMatch))
        {
            if (ifNoneMatch is "*" && currentEtag is not null) return Precondition.Failed;
            if (quoted is not null && ifNoneMatch.Contains(quoted, StringComparison.Ordinal))
                return Precondition.Failed;
        }

        return Precondition.Pass;
    }

    public bool HasWriteConditions(IHeaderDictionary headers) =>
        !string.IsNullOrEmpty(headers["If-Match"].ToString())
        || !string.IsNullOrEmpty(headers["If-None-Match"].ToString());

    private bool TryParseHttpDate(string s, out DateTimeOffset dt)
    {
        if (HeaderUtilities.TryParseDate(s, out dt)) return true;
        var afterDay = s.IndexOf(", ", StringComparison.Ordinal);
        return afterDay >= 0
            && DateTimeOffset.TryParseExact(
                s[(afterDay + 2)..],
                "dd MMM yyyy HH:mm:ss 'GMT'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out dt);
    }

    private DateTimeOffset TruncateToSecond(DateTimeOffset value)
    {
        var ticks = value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerSecond);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
