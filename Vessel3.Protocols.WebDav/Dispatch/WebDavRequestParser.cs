using Microsoft.AspNetCore.Http;

namespace Vessel3.Protocols.WebDav.Dispatch;

internal static class WebDavRequestParser
{
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
        var depth = ResolveDepth(req.Headers[WebDavHeaders.Depth].ToString(), operation);

        var destination = req.Headers[WebDavHeaders.Destination].ToString();
        var overwrite = !string.Equals(req.Headers[WebDavHeaders.Overwrite].ToString(), "F", StringComparison.OrdinalIgnoreCase);

        return new WebDavRequestTarget(
            operation,
            bucket,
            itemPath,
            depth,
            string.IsNullOrEmpty(destination) ? null : destination,
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
        [var b] => (Uri.UnescapeDataString(b), null),
        [var b, .. var rest] => (Uri.UnescapeDataString(b), FormatKey(rest, isTrailingSlash))
    };

    private static string FormatKey(string[] segments, bool isTrailingSlash)
    {
        var unescaped = string.Join('/', segments.Select(Uri.UnescapeDataString));
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

    private static WebDavOperationKind ResolveOperation(string method) => method.ToUpperInvariant() switch
    {
        "OPTIONS" => WebDavOperationKind.Options,
        "PROPFIND" => WebDavOperationKind.Propfind,
        "GET" => WebDavOperationKind.Get,
        "HEAD" => WebDavOperationKind.Head,
        "PUT" => WebDavOperationKind.Put,
        "DELETE" => WebDavOperationKind.Delete,
        "MKCOL" => WebDavOperationKind.Mkcol,
        "MOVE" => WebDavOperationKind.Move,
        "COPY" => WebDavOperationKind.Copy,
        "LOCK" => WebDavOperationKind.Lock,
        "UNLOCK" => WebDavOperationKind.Unlock,
        "PROPPATCH" => WebDavOperationKind.Proppatch,
        _ => WebDavOperationKind.Unknown
    };

    private static int ResolveDepth(string? depthHeader, WebDavOperationKind operation) =>
        string.IsNullOrEmpty(depthHeader)
            ? operation is WebDavOperationKind.Propfind ? 1 : int.MaxValue
            : depthHeader.Trim().ToLowerInvariant() switch
            {
                "0" => 0,
                "1" => 1,
                _ => int.MaxValue
            };
}
