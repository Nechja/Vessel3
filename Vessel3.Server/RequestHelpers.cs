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
        start = 0;
        end = 0;
        const string prefix = "bytes=";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = raw[prefix.Length..];
        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0) return false;
        if (!long.TryParse(rest[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out start)) return false;
        var endStr = rest[(dash + 1)..];
        end = string.IsNullOrEmpty(endStr) ? size - 1
            : long.TryParse(endStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e
            : -1;
        return end >= start && end < size;
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
