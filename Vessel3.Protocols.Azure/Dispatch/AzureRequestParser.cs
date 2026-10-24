using Microsoft.AspNetCore.Http;

namespace Vessel3.Protocols.Azure.Dispatch;

internal static class AzureRequestParser
{
    public const string DevStoreAccount = "devstoreaccount1";
    private const string DevStorePathPrefix = "/" + DevStoreAccount;

    public static bool IsAzureRequest(HttpRequest req)
    {
        var headers = req.Headers;
        if (headers.ContainsKey("x-ms-version") || headers.ContainsKey("x-ms-date"))
        {
            return true;
        }

        if (headers.TryGetValue("Authorization", out var authHdr) && authHdr.Count > 0 &&
            authHdr[0] is { Length: > 0 } authStr &&
            (authStr.StartsWith("SharedKey ", StringComparison.OrdinalIgnoreCase) ||
             authStr.StartsWith("SharedKeyLite ", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var query = req.Query;
        if (query.ContainsKey("sig") && (query.ContainsKey("se") || query.ContainsKey("sp") || query.ContainsKey("sv")))
        {
            return true;
        }

        var path = req.Path.Value;
        return path is not null &&
            (path.StartsWith(DevStorePathPrefix, StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/V3AK", StringComparison.OrdinalIgnoreCase));
    }

    public static AzureRequestTarget Parse(HttpRequest req)
    {
        var rawPath = req.Path.Value ?? "/";
        var segments = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var authAccount = ExtractAccountFromAuthHeader(req);
        string? account = null;
        string? container = null;
        string? blob = null;

        var idx = 0;
        if (segments.Length > 0)
        {
            if (string.Equals(segments[0], DevStoreAccount, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(authAccount) && string.Equals(segments[0], authAccount, StringComparison.OrdinalIgnoreCase)) ||
                segments[0].StartsWith("V3AK", StringComparison.OrdinalIgnoreCase))
            {
                account = segments[0];
                idx = 1;
            }
        }

        account ??= authAccount;

        if (idx < segments.Length)
        {
            container = segments[idx].Contains('%') ? Uri.UnescapeDataString(segments[idx]) : segments[idx];
            idx++;
        }

        if (idx < segments.Length)
        {
            var remaining = segments[idx..];
            var needsUnescape = false;
            foreach (var seg in remaining)
            {
                if (seg.Contains('%'))
                {
                    needsUnescape = true;
                    break;
                }
            }
            blob = needsUnescape
                ? string.Join('/', remaining.Select(Uri.UnescapeDataString))
                : string.Join('/', remaining);
        }

        var method = req.Method;
        var query = req.Query;
        var comp = query.TryGetValue("comp", out var compValues) && compValues.Count > 0 ? compValues[0] ?? "" : "";
        var restype = query.TryGetValue("restype", out var restypeValues) && restypeValues.Count > 0 ? restypeValues[0] ?? "" : "";

        var op = ResolveOperation(method, container, blob, restype, comp, req.Headers);

        return new AzureRequestTarget(op, account, container, blob);
    }

    private static string? ExtractAccountFromAuthHeader(HttpRequest req)
    {
        if (!req.Headers.TryGetValue("Authorization", out var authVal) || authVal.Count == 0 || authVal[0] is not { Length: > 0 } auth)
            return null;

        const string sharedKeyPrefix = "SharedKey ";
        const string sharedKeyLitePrefix = "SharedKeyLite ";

        if (auth.StartsWith(sharedKeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var token = auth[sharedKeyPrefix.Length..].Trim();
            var colon = token.IndexOf(':');
            return colon > 0 ? token[..colon] : null;
        }

        if (auth.StartsWith(sharedKeyLitePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var token = auth[sharedKeyLitePrefix.Length..].Trim();
            var colon = token.IndexOf(':');
            return colon > 0 ? token[..colon] : null;
        }

        return null;
    }

    private static AzureOperationKind ResolveOperation(
        string method,
        string? container,
        string? blob,
        string restype,
        string comp,
        IHeaderDictionary headers) =>
        string.IsNullOrEmpty(container)
            ? ResolveServiceOperation(method, restype, comp)
            : string.IsNullOrEmpty(blob)
                ? ResolveContainerOperation(method, restype, comp)
                : ResolveBlobOperation(method, comp, headers);

    private static AzureOperationKind ResolveServiceOperation(string method, string restype, string comp)
    {
        var r = restype.ToLowerInvariant();
        var c = comp.ToLowerInvariant();

        if (HttpMethods.IsGet(method))
        {
            return (r, c) switch
            {
                ("", "list") => AzureOperationKind.ListContainers,
                ("service", "properties") => AzureOperationKind.GetServiceProperties,
                ("account", "properties") => AzureOperationKind.GetAccountInfo,
                _ => AzureOperationKind.Unknown
            };
        }

        if (HttpMethods.IsPut(method) && r is "service" && c is "properties")
        {
            return AzureOperationKind.SetServiceProperties;
        }

        return AzureOperationKind.Unknown;
    }

    private static AzureOperationKind ResolveContainerOperation(string method, string restype, string comp)
    {
        var r = restype.ToLowerInvariant();
        var c = comp.ToLowerInvariant();

        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
        {
            return (r, c) switch
            {
                (_, "list") => AzureOperationKind.ListBlobs,
                (_, "metadata") => AzureOperationKind.GetContainerMetadata,
                (_, "acl") => AzureOperationKind.GetContainerAcl,
                ("container", "") or ("", "") => AzureOperationKind.GetContainerProperties,
                _ => AzureOperationKind.Unknown
            };
        }

        if (HttpMethods.IsPut(method))
        {
            return (r, c) switch
            {
                ("container", "") => AzureOperationKind.CreateContainer,
                (_, "metadata") => AzureOperationKind.SetContainerMetadata,
                (_, "acl") => AzureOperationKind.SetContainerAcl,
                _ => AzureOperationKind.Unknown
            };
        }

        if (HttpMethods.IsDelete(method) && r is "container" && c is "")
        {
            return AzureOperationKind.DeleteContainer;
        }

        return AzureOperationKind.Unknown;
    }

    private static AzureOperationKind ResolveBlobOperation(string method, string comp, IHeaderDictionary headers)
    {
        var lowerComp = comp.ToLowerInvariant();

        if (HttpMethods.IsPut(method))
        {
            return lowerComp switch
            {
                "block" => AzureOperationKind.PutBlock,
                "blocklist" => AzureOperationKind.PutBlockList,
                "metadata" => AzureOperationKind.SetBlobMetadata,
                "tags" => AzureOperationKind.PutBlobTags,
                _ => headers.ContainsKey("x-ms-copy-source") ? AzureOperationKind.CopyBlob : AzureOperationKind.PutBlob
            };
        }

        if (HttpMethods.IsGet(method))
        {
            return lowerComp switch
            {
                "blocklist" => AzureOperationKind.GetBlockList,
                "metadata" => AzureOperationKind.GetBlobMetadata,
                "tags" => AzureOperationKind.GetBlobTags,
                _ => AzureOperationKind.GetBlob
            };
        }

        if (HttpMethods.IsHead(method))
        {
            return AzureOperationKind.HeadBlob;
        }

        if (HttpMethods.IsDelete(method))
        {
            return AzureOperationKind.DeleteBlob;
        }

        return AzureOperationKind.Unknown;
    }
}
