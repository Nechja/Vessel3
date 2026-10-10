using System.Globalization;
using Microsoft.AspNetCore.Http;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native.Headers;

internal static class NativeHeaderCodec
{
    private const string VersionIdHeader = "X-Vessel-Version-Id";
    private const string MetaHeaderPrefix = "X-Vessel-Meta-";

    public static void ApplyObjectHeaders(IHeaderDictionary headers, StoredObject obj, string? versionId)
    {
        ApplyCommonHeaders(headers, obj.Etag, obj.ContentType, obj.Size, obj.LastModified, versionId ?? obj.VersionId, obj.Metadata);
    }

    public static void ApplyObjectHeaders(IHeaderDictionary headers, ObjectStat stat, string? versionId)
    {
        ApplyCommonHeaders(headers, stat.Etag, stat.ContentType, stat.Size, stat.LastModified, versionId ?? stat.VersionId, stat.Metadata);
    }

    private static void ApplyCommonHeaders(
        IHeaderDictionary headers,
        string etag,
        string? contentType,
        long size,
        DateTimeOffset lastModified,
        string? versionId,
        IReadOnlyDictionary<string, string>? metadata)
    {
        headers.ETag = $"\"{etag}\"";
        if (contentType is { Length: > 0 })
        {
            headers.ContentType = contentType;
        }
        headers.ContentLength = size;
        headers.LastModified = lastModified.ToString("R", CultureInfo.InvariantCulture);

        if (versionId is not null)
        {
            headers[VersionIdHeader] = versionId;
        }

        if (metadata is null) return;

        foreach (var (k, v) in metadata)
        {
            headers[$"{MetaHeaderPrefix}{k}"] = v;
        }
    }

    public static IReadOnlyDictionary<string, string> ExtractMetadata(IHeaderDictionary headers)
    {
        Dictionary<string, string>? metadata = null;
        foreach (var header in headers)
        {
            if (!header.Key.StartsWith(MetaHeaderPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var metaKey = header.Key[MetaHeaderPrefix.Length..];
            metadata[metaKey] = header.Value.ToString();
        }

        return metadata ?? (IReadOnlyDictionary<string, string>)System.Collections.Frozen.FrozenDictionary<string, string>.Empty;
    }

    public static string? Nullify(string? s) => string.IsNullOrEmpty(s) ? null : s;
    public static string? Nullify(Microsoft.Extensions.Primitives.StringValues sv) =>
        sv.Count > 0 && sv[0] is { Length: > 0 } s ? s : null;
}
