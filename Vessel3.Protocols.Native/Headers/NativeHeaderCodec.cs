using System.Globalization;
using Microsoft.AspNetCore.Http;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native.Headers;

internal static class NativeHeaderCodec
{
    private const string VersionIdHeader = "X-Vessel-Version-Id";
    private const string MetaHeaderPrefix = "X-Vessel-Meta-";

    public static void ApplyObjectHeaders(IHeaderDictionary headers, StoredObject obj, string? versionId, string? currentVersionId)
    {
        ApplyCommonHeaders(headers, obj.Etag, obj.ContentType, obj.Size, obj.LastModified, versionId, currentVersionId, obj.Metadata);
    }

    public static void ApplyObjectHeaders(IHeaderDictionary headers, ObjectStat stat, string? versionId, string? currentVersionId)
    {
        ApplyCommonHeaders(headers, stat.Etag, stat.ContentType, stat.Size, stat.LastModified, versionId, currentVersionId, stat.Metadata);
    }

    private static void ApplyCommonHeaders(
        IHeaderDictionary headers,
        string etag,
        string? contentType,
        long size,
        DateTimeOffset lastModified,
        string? versionId,
        string? currentVersionId,
        IReadOnlyDictionary<string, string>? metadata)
    {
        headers.ETag = $"\"{etag}\"";
        if (contentType is { Length: > 0 })
        {
            headers.ContentType = contentType;
        }
        headers.ContentLength = size;
        headers.LastModified = lastModified.ToString("R", CultureInfo.InvariantCulture);

        var effectiveVersion = versionId ?? currentVersionId;
        if (effectiveVersion is not null)
        {
            headers[VersionIdHeader] = effectiveVersion;
        }

        if (metadata is null) return;

        foreach (var (k, v) in metadata)
        {
            headers[$"{MetaHeaderPrefix}{k}"] = v;
        }
    }

    public static Dictionary<string, string> ExtractMetadata(IHeaderDictionary headers)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (!header.Key.StartsWith(MetaHeaderPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var metaKey = header.Key[MetaHeaderPrefix.Length..];
            metadata[metaKey] = header.Value.ToString();
        }

        return metadata;
    }

    public static string? Nullify(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
