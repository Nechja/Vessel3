using System.Collections.Frozen;
using Microsoft.Extensions.Primitives;

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

        Dictionary<string, string>? meta = null;
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

            meta ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            meta[key] = values.ToString();
        }

        return meta ?? (IReadOnlyDictionary<string, string>)FrozenDictionary<string, string>.Empty;
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
            if (!headers.TryGetValue(name, out var val) || StringValues.IsNullOrEmpty(val))
            {
                continue;
            }

            dict ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            dict[name] = val.ToString();
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
            IfMatch: HeaderValueOrNull(headers, "If-Match"),
            IfNoneMatch: HeaderValueOrNull(headers, "If-None-Match"),
            IfModifiedSince: HeaderValueOrNull(headers, "If-Modified-Since"),
            IfUnmodifiedSince: HeaderValueOrNull(headers, "If-Unmodified-Since"));

    public static PreconditionRules ExtractCopySourcePreconditions(IHeaderDictionary headers) =>
        new(
            IfMatch: HeaderValueOrNull(headers, "x-amz-copy-source-if-match"),
            IfNoneMatch: HeaderValueOrNull(headers, "x-amz-copy-source-if-none-match"),
            IfModifiedSince: HeaderValueOrNull(headers, "x-amz-copy-source-if-modified-since"),
            IfUnmodifiedSince: HeaderValueOrNull(headers, "x-amz-copy-source-if-unmodified-since"));

    public static WritePreconditions ExtractWritePreconditions(IHeaderDictionary headers) =>
        new(
            IfMatch: HeaderValueOrNull(headers, "If-Match"),
            IfNoneMatch: HeaderValueOrNull(headers, "If-None-Match"));

    private static string? HeaderValueOrNull(IHeaderDictionary headers, string name) =>
        headers.TryGetValue(name, out var v) && !StringValues.IsNullOrEmpty(v) ? v.ToString() : null;
}
