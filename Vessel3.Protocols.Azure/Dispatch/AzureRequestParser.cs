using Microsoft.AspNetCore.Http;

namespace Vessel3.Protocols.Azure.Dispatch;

internal static class AzureRequestParser
{
    public const string DevStoreAccount = "devstoreaccount1";

    public static bool IsAzureRequest(HttpRequest req) =>
        req.Headers.ContainsKey("x-ms-version") ||
        req.Headers.ContainsKey("x-ms-date") ||
        (req.Headers.TryGetValue("Authorization", out var authHdr) && authHdr.Count > 0 &&
         (authHdr[0]?.StartsWith("SharedKey ", StringComparison.OrdinalIgnoreCase) is true ||
          authHdr[0]?.StartsWith("SharedKeyLite ", StringComparison.OrdinalIgnoreCase) is true)) ||
        (req.Query.ContainsKey("sig") && (req.Query.ContainsKey("se") || req.Query.ContainsKey("sp") || req.Query.ContainsKey("sv"))) ||
        (req.Path.Value ?? "/").StartsWith($"/{DevStoreAccount}", StringComparison.OrdinalIgnoreCase) ||
        (req.Path.Value ?? "/").StartsWith("/V3AK", StringComparison.OrdinalIgnoreCase);

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
            container = Uri.UnescapeDataString(segments[idx]);
            idx++;
        }

        if (idx < segments.Length)
        {
            blob = string.Join('/', segments[idx..].Select(Uri.UnescapeDataString));
        }

        var method = req.Method;
        var query = req.Query;
        var comp = query.TryGetValue("comp", out var cVal) && cVal.Count > 0 ? cVal[0] ?? "" : "";
        var restype = query.TryGetValue("restype", out var rVal) && rVal.Count > 0 ? rVal[0] ?? "" : "";

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

    private static AzureOperationKind ResolveServiceOperation(string method, string restype, string comp) =>
        (HttpMethods.IsGet(method), HttpMethods.IsPut(method), restype.ToLowerInvariant(), comp.ToLowerInvariant()) switch
        {
            (true, false, "", "list") => AzureOperationKind.ListContainers,
            (true, false, "service", "properties") => AzureOperationKind.GetServiceProperties,
            (false, true, "service", "properties") => AzureOperationKind.SetServiceProperties,
            (true, false, "account", "properties") => AzureOperationKind.GetAccountInfo,
            _ => AzureOperationKind.Unknown
        };

    private static AzureOperationKind ResolveContainerOperation(string method, string restype, string comp) =>
        (HttpMethods.IsGet(method), HttpMethods.IsHead(method), HttpMethods.IsPut(method), HttpMethods.IsDelete(method), restype.ToLowerInvariant(), comp.ToLowerInvariant()) switch
        {
            (false, false, true, false, "container", "") => AzureOperationKind.CreateContainer,
            (false, false, false, true, "container", "") => AzureOperationKind.DeleteContainer,
            (true, _, false, false, "container", "") or (_, true, false, false, "container", "") => AzureOperationKind.GetContainerProperties,
            (true, false, false, false, _, "list") => AzureOperationKind.ListBlobs,
            (true, false, false, false, _, "metadata") => AzureOperationKind.GetContainerMetadata,
            (false, false, true, false, _, "metadata") => AzureOperationKind.SetContainerMetadata,
            (true, false, false, false, _, "acl") => AzureOperationKind.GetContainerAcl,
            (false, false, true, false, _, "acl") => AzureOperationKind.SetContainerAcl,
            (true, _, false, false, "", "") or (_, true, false, false, "", "") => AzureOperationKind.GetContainerProperties,
            _ => AzureOperationKind.Unknown
        };

    private static AzureOperationKind ResolveBlobOperation(string method, string comp, IHeaderDictionary headers) =>
        HttpMethods.IsPut(method)
            ? comp.ToLowerInvariant() switch
            {
                "block" => AzureOperationKind.PutBlock,
                "blocklist" => AzureOperationKind.PutBlockList,
                "metadata" => AzureOperationKind.SetBlobMetadata,
                "tags" => AzureOperationKind.PutBlobTags,
                _ => headers.ContainsKey("x-ms-copy-source") ? AzureOperationKind.CopyBlob : AzureOperationKind.PutBlob
            }
            : HttpMethods.IsGet(method)
                ? comp.ToLowerInvariant() switch
                {
                    "blocklist" => AzureOperationKind.GetBlockList,
                    "metadata" => AzureOperationKind.GetBlobMetadata,
                    "tags" => AzureOperationKind.GetBlobTags,
                    _ => AzureOperationKind.GetBlob
                }
                : HttpMethods.IsHead(method)
                    ? AzureOperationKind.HeadBlob
                    : HttpMethods.IsDelete(method)
                        ? AzureOperationKind.DeleteBlob
                        : AzureOperationKind.Unknown;
}
