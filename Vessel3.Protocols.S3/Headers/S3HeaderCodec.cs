using System.Collections.Frozen;

namespace Vessel3.Server.S3;

internal static class S3HeaderCodec
{
    private const string UserMetaPrefix = "x-amz-meta-";

    private static readonly string[] SystemHeaderNames =
    [
        "Content-Disposition",
        "Content-Language",
        "Content-Encoding",
        "Cache-Control",
        "Expires",
    ];

    public static IReadOnlyDictionary<string, string> ExtractUserMetadata(IHeaderDictionary headers)
    {
        if (headers.Count == 0)
        {
            return FrozenDictionary<string, string>.Empty;
        }

        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
        {
            if (!name.StartsWith(UserMetaPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = name[UserMetaPrefix.Length..].ToLowerInvariant();
            if (key.Length is 0)
            {
                continue;
            }

            meta[key] = values.ToString();
        }

        return meta;
    }

    public static IReadOnlyDictionary<string, string>? ExtractSystemHeaders(IHeaderDictionary headers)
    {
        if (headers.Count == 0)
        {
            return null;
        }

        Dictionary<string, string>? dict = null;
        foreach (ref readonly var name in SystemHeaderNames.AsSpan())
        {
            var v = headers[name].ToString();
            if (string.IsNullOrEmpty(v))
            {
                continue;
            }

            dict ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            dict[name] = v;
        }

        return dict;
    }

    public static void EmitSystemHeaders(IHeaderDictionary headers, IReadOnlyDictionary<string, string>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (ref readonly var name in SystemHeaderNames.AsSpan())
        {
            if (source.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v))
            {
                headers[name] = v;
            }
        }
    }

    public static PreconditionRules ExtractReadPreconditions(IHeaderDictionary headers) =>
        new(
            IfMatch: NullIfEmpty(headers["If-Match"].ToString()),
            IfNoneMatch: NullIfEmpty(headers["If-None-Match"].ToString()),
            IfModifiedSince: NullIfEmpty(headers["If-Modified-Since"].ToString()),
            IfUnmodifiedSince: NullIfEmpty(headers["If-Unmodified-Since"].ToString()));

    public static PreconditionRules ExtractCopySourcePreconditions(IHeaderDictionary headers) =>
        new(
            IfMatch: NullIfEmpty(headers["x-amz-copy-source-if-match"].ToString()),
            IfNoneMatch: NullIfEmpty(headers["x-amz-copy-source-if-none-match"].ToString()),
            IfModifiedSince: NullIfEmpty(headers["x-amz-copy-source-if-modified-since"].ToString()),
            IfUnmodifiedSince: NullIfEmpty(headers["x-amz-copy-source-if-unmodified-since"].ToString()));

    public static WritePreconditions ExtractWritePreconditions(IHeaderDictionary headers) =>
        new(
            IfMatch: NullIfEmpty(headers["If-Match"].ToString()),
            IfNoneMatch: NullIfEmpty(headers["If-None-Match"].ToString()));

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
