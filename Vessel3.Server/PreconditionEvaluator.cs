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
    Precondition EvaluateCopySource(IHeaderDictionary headers, string etag, DateTimeOffset lastModified);
    bool HasWriteConditions(IHeaderDictionary headers);
}

internal sealed class PreconditionEvaluator : IPreconditionEvaluator
{
    public Precondition EvaluateForRead(IHeaderDictionary headers, string etag, DateTimeOffset lastModified) =>
        EvaluateRead(
            headers["If-Match"].ToString(),
            headers["If-None-Match"].ToString(),
            headers["If-Modified-Since"].ToString(),
            headers["If-Unmodified-Since"].ToString(),
            etag, lastModified);

    public Precondition EvaluateCopySource(IHeaderDictionary headers, string etag, DateTimeOffset lastModified) =>
        EvaluateRead(
            headers["x-amz-copy-source-if-match"].ToString(),
            headers["x-amz-copy-source-if-none-match"].ToString(),
            headers["x-amz-copy-source-if-modified-since"].ToString(),
            headers["x-amz-copy-source-if-unmodified-since"].ToString(),
            etag, lastModified);

    public Precondition EvaluateForWrite(IHeaderDictionary headers, string? currentEtag)
    {
        var ifMatch = headers["If-Match"].ToString();
        var ifNoneMatch = headers["If-None-Match"].ToString();

        if (!string.IsNullOrEmpty(ifMatch) && ifMatch is not "*")
        {
            if (currentEtag is null) return Precondition.Failed;
            if (!EtagListContains(ifMatch, currentEtag)) return Precondition.Failed;
        }

        if (!string.IsNullOrEmpty(ifNoneMatch))
        {
            if (ifNoneMatch is "*" && currentEtag is not null) return Precondition.Failed;
            if (currentEtag is not null && EtagListContains(ifNoneMatch, currentEtag))
                return Precondition.Failed;
        }

        return Precondition.Pass;
    }

    public bool HasWriteConditions(IHeaderDictionary headers) =>
        !string.IsNullOrEmpty(headers["If-Match"].ToString())
        || !string.IsNullOrEmpty(headers["If-None-Match"].ToString());

    private Precondition EvaluateRead(string ifMatch, string ifNoneMatch, string ifModSince, string ifUnmodSince, string etag, DateTimeOffset lastModified)
    {
        var lastModSec = TruncateToSecond(lastModified);

        if (!string.IsNullOrEmpty(ifMatch) && ifMatch is not "*"
            && !EtagListContains(ifMatch, etag))
            return Precondition.Failed;

        if (!string.IsNullOrEmpty(ifUnmodSince)
            && TryParseHttpDate(ifUnmodSince, out var unmodSince)
            && lastModSec > unmodSince)
            return Precondition.Failed;

        var noneMatchHit = !string.IsNullOrEmpty(ifNoneMatch)
            && (ifNoneMatch is "*" || EtagListContains(ifNoneMatch, etag));
        var modSinceHit = string.IsNullOrEmpty(ifNoneMatch)
            && !string.IsNullOrEmpty(ifModSince)
            && TryParseHttpDate(ifModSince, out var modSince)
            && lastModSec <= modSince;

        return noneMatchHit || modSinceHit ? Precondition.NotModified : Precondition.Pass;
    }

    private static bool EtagListContains(string headerValue, string etag)
    {
        foreach (var raw in headerValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.StartsWith("W/", StringComparison.Ordinal) ? raw[2..] : raw;
            if (t.Length >= 2 && t[0] == '"' && t[^1] == '"') t = t[1..^1];
            if (string.Equals(t, etag, StringComparison.Ordinal)) return true;
        }
        return false;
    }

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
