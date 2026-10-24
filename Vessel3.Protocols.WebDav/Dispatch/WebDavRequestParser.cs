using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;

namespace Vessel3.Protocols.WebDav.Dispatch;

internal static class WebDavRequestParser
{
    private static readonly FrozenDictionary<string, WebDavOperationKind> Operations =
        new Dictionary<string, WebDavOperationKind>(StringComparer.OrdinalIgnoreCase)
        {
            [HttpMethods.Get] = WebDavOperationKind.Get,
            [HttpMethods.Head] = WebDavOperationKind.Head,
            [HttpMethods.Put] = WebDavOperationKind.Put,
            [HttpMethods.Delete] = WebDavOperationKind.Delete,
            [HttpMethods.Options] = WebDavOperationKind.Options,
            ["PROPFIND"] = WebDavOperationKind.Propfind,
            ["MKCOL"] = WebDavOperationKind.Mkcol,
            ["MOVE"] = WebDavOperationKind.Move,
            ["COPY"] = WebDavOperationKind.Copy,
            ["LOCK"] = WebDavOperationKind.Lock,
            ["UNLOCK"] = WebDavOperationKind.Unlock,
            ["PROPPATCH"] = WebDavOperationKind.Proppatch,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    public static bool IsWebDavRequest(HttpRequest req)
    {
        var path = req.Path.Value ?? "/";
        return path.StartsWith(WebDavRoutes.DavPrefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(WebDavRoutes.WebDavPrefix, StringComparison.OrdinalIgnoreCase)
            || req.Method is "PROPFIND" or "PROPPATCH" or "MKCOL" or "COPY" or "MOVE" or "LOCK" or "UNLOCK"
            || req.Headers.ContainsKey(WebDavHeaders.Translate);
    }

    public static WebDavRequestTarget Parse(HttpRequest req)
    {
        var rawPath = req.Path.Value ?? "/";
        var isTrailingSlash = rawPath.EndsWith('/');
        var strippedPath = StripPathPrefix(rawPath);

        var segments = strippedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var (bucket, itemPath) = ExtractBucketAndPath(segments, isTrailingSlash);
        var isCollection = isTrailingSlash || segments.Length <= 1;

        var operation = ResolveOperation(req.Method);
        var depth = ResolveDepth(req.Headers[WebDavHeaders.Depth], operation);

        string? destination = null;
        if (operation is WebDavOperationKind.Copy or WebDavOperationKind.Move
            && req.Headers.TryGetValue(WebDavHeaders.Destination, out var destVal) && destVal.Count > 0)
        {
            destination = destVal[0];
        }

        var overwrite = !(operation is WebDavOperationKind.Copy or WebDavOperationKind.Move
            && req.Headers.TryGetValue(WebDavHeaders.Overwrite, out var ovrVal)
            && string.Equals(ovrVal, "F", StringComparison.OrdinalIgnoreCase));

        return new WebDavRequestTarget(
            operation,
            bucket,
            itemPath,
            depth,
            destination,
            overwrite,
            isCollection);
    }

    public static (string? Bucket, string? Key) ParseDestination(string destinationUri)
    {
        var rawPath = Uri.TryCreate(destinationUri, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : destinationUri;

        var strippedPath = StripPathPrefix(rawPath);
        var segments = strippedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return ExtractBucketAndPath(segments, strippedPath.EndsWith('/'));
    }

    private static (string? Bucket, string? Key) ExtractBucketAndPath(string[] segments, bool isTrailingSlash) => segments switch
    {
        [] => (null, null),
        [var b] => (b.Contains('%') ? Uri.UnescapeDataString(b) : b, null),
        [var b, .. var rest] => (b.Contains('%') ? Uri.UnescapeDataString(b) : b, FormatKey(rest, isTrailingSlash))
    };

    private static string FormatKey(string[] segments, bool isTrailingSlash)
    {
        var needsUnescape = false;
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Contains('%'))
            {
                needsUnescape = true;
                break;
            }
        }

        var unescaped = needsUnescape
            ? string.Join('/', segments.Select(Uri.UnescapeDataString))
            : string.Join('/', segments);

        return isTrailingSlash && !unescaped.EndsWith('/')
            ? unescaped + "/"
            : unescaped;
    }

    private static string StripPathPrefix(string path) => path switch
    {
        _ when path.StartsWith(WebDavRoutes.DavPrefixWithSlash, StringComparison.OrdinalIgnoreCase) => path[WebDavRoutes.DavPrefix.Length..],
        _ when path.Equals(WebDavRoutes.DavPrefix, StringComparison.OrdinalIgnoreCase) => WebDavRoutes.Root,
        _ when path.StartsWith(WebDavRoutes.WebDavPrefixWithSlash, StringComparison.OrdinalIgnoreCase) => path[WebDavRoutes.WebDavPrefix.Length..],
        _ when path.Equals(WebDavRoutes.WebDavPrefix, StringComparison.OrdinalIgnoreCase) => WebDavRoutes.Root,
        _ => path
    };

    private static WebDavOperationKind ResolveOperation(string method) =>
        Operations.GetValueOrDefault(method, WebDavOperationKind.Unknown);

    private static int ResolveDepth(Microsoft.Extensions.Primitives.StringValues depthHeader, WebDavOperationKind operation)
    {
        if (depthHeader.Count == 0 || depthHeader[0] is not { Length: > 0 } val)
        {
            return operation is WebDavOperationKind.Propfind ? 1 : int.MaxValue;
        }

        var span = val.AsSpan().Trim();
        if (span.SequenceEqual("0")) return 0;
        if (span.SequenceEqual("1")) return 1;
        return int.MaxValue;
    }
}
