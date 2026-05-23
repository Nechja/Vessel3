using System.Globalization;
using Vessel3.Server.S3;

namespace Vessel3.Server;

internal static class RequestHelpers
{
    public static IReadOnlyDictionary<string, string> ExtractUserMetadata(IHeaderDictionary headers)
    {
        const string prefix = "x-amz-meta-";
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var key = name[prefix.Length..].ToLowerInvariant();
            if (key.Length is 0) continue;
            meta[key] = values.ToString();
        }
        return meta;
    }

    public static string? Nullify(string? s) => string.IsNullOrEmpty(s) ? null : s;

    public static long ParseAgeQuery(IQueryCollection query, string name, long fallback)
    {
        var raw = Nullify(query[name].ToString()) ?? Nullify(query["x-" + name].ToString());
        return raw is not null && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : fallback;
    }

    public static (Stream Body, long? DeclaredLength) DecodeRequestBody(HttpRequest req)
    {
        var contentSha = req.Headers["x-amz-content-sha256"].ToString();
        var isChunked = req.Headers.ContentEncoding.ToString().Contains("aws-chunked", StringComparison.Ordinal)
            || contentSha.Contains("STREAMING-", StringComparison.Ordinal);
        if (!isChunked) return (req.Body, req.ContentLength);

        var sigCtx = req.HttpContext.Items["sigctx"] as SignatureContext;
        var declared = long.TryParse(req.Headers["x-amz-decoded-content-length"].ToString(), out var dl) ? dl : (long?)null;
        return (new AwsChunkedStream(req.Body, sigCtx), declared);
    }

    public static bool TryParseByteRange(string raw, long size, out long start, out long end)
    {
        if (ParseByteRange(raw, size) is ByteRange.Normal n)
        {
            start = n.Start;
            end = n.End;
            return true;
        }
        start = 0;
        end = 0;
        return false;
    }

    /// <summary>
    /// Tri-state range parse outcome.
    /// - <see cref="Normal"/>: single satisfiable range; serve 206 with [Start..End].
    /// - <see cref="Unsatisfiable"/>: syntactically valid but start beyond EOF; emit 416 with Content-Range bytes */size.
    /// - <see cref="Ignored"/>: header should be ignored entirely (multi-range, malformed,
    ///   or non-"bytes=" units). Caller returns 200 full body. S3 returns full object on multi-range.
    /// </summary>
    internal abstract record ByteRange
    {
        internal sealed record Normal(long Start, long End) : ByteRange;
        internal sealed record Unsatisfiable : ByteRange;
        internal sealed record Ignored : ByteRange;
    }

    public static ByteRange ParseByteRange(string raw, long size)
    {
        if (string.IsNullOrEmpty(raw)) return new ByteRange.Ignored();
        const string prefix = "bytes=";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return new ByteRange.Ignored();
        var rest = raw[prefix.Length..].Trim();

        // Multi-range (comma-separated): S3 returns full body; ignore the header.
        if (rest.Contains(',', StringComparison.Ordinal)) return new ByteRange.Ignored();

        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0) return new ByteRange.Ignored();

        var startStr = rest[..dash];
        var endStr = rest[(dash + 1)..];

        // Suffix range: bytes=-N → last N bytes.
        if (string.IsNullOrEmpty(startStr))
        {
            if (!long.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0)
                return new ByteRange.Ignored();
            if (n is 0) return new ByteRange.Unsatisfiable();
            if (size is 0) return new ByteRange.Unsatisfiable();
            var clamped = Math.Min(n, size);
            return new ByteRange.Normal(size - clamped, size - 1);
        }

        if (!long.TryParse(startStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) || start < 0)
            return new ByteRange.Ignored();

        // Start beyond EOF: 416.
        if (start >= size) return new ByteRange.Unsatisfiable();

        // Open end: bytes=N- → N..size-1.
        if (string.IsNullOrEmpty(endStr)) return new ByteRange.Normal(start, size - 1);

        if (!long.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var end) || end < start)
            return new ByteRange.Ignored();

        // Clamp end to last byte (RFC 7233).
        if (end >= size) end = size - 1;
        return new ByteRange.Normal(start, end);
    }

    public static bool TryParseCopySource(string raw, out string bucket, out string key)
    {
        bucket = string.Empty;
        key = string.Empty;
        var trimmed = raw.StartsWith('/') ? raw[1..] : raw;
        var qm = trimmed.IndexOf('?', StringComparison.Ordinal);
        if (qm >= 0) trimmed = trimmed[..qm];
        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == trimmed.Length - 1) return false;
        bucket = trimmed[..slash];
        key = Uri.UnescapeDataString(trimmed[(slash + 1)..]);
        return true;
    }
}
