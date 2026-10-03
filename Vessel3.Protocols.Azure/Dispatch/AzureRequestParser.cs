using Microsoft.AspNetCore.Http;

namespace Vessel3.Protocols.Azure.Dispatch;

internal static class AzureRequestParser
{
    public const string DevStoreAccount = "devstoreaccount1";

    public static bool IsAzureRequest(HttpRequest req) =>
        req.Headers.ContainsKey("x-ms-version") ||
        req.Headers.ContainsKey("x-ms-date") ||
        req.Headers.Authorization.ToString().StartsWith("SharedKey ", StringComparison.OrdinalIgnoreCase) ||
        req.Headers.Authorization.ToString().StartsWith("SharedKeyLite ", StringComparison.OrdinalIgnoreCase) ||
        (req.Query.ContainsKey("sig") && (req.Query.ContainsKey("se") || req.Query.ContainsKey("sp") || req.Query.ContainsKey("sv"))) ||
        (req.Path.Value ?? "/").StartsWith($"/{DevStoreAccount}", StringComparison.OrdinalIgnoreCase);

    public static AzureRequestTarget Parse(HttpRequest req)
    {
        var rawPath = req.Path.Value ?? "/";
        var segments = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        string? account = null;
        string? container = null;
        string? blob = null;

        var idx = 0;
        if (segments.Length > 0 && string.Equals(segments[0], DevStoreAccount, StringComparison.OrdinalIgnoreCase))
        {
            account = segments[0];
            idx = 1;
        }

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
        var comp = query["comp"].ToString();
        var restype = query["restype"].ToString();

        var op = ResolveOperation(method, container, blob, restype, comp, req.Headers);

        return new AzureRequestTarget(op, account, container, blob);
    }

    private static AzureOperationKind ResolveOperation(
        string method,
        string? container,
        string? blob,
        string restype,
        string comp,
        IHeaderDictionary headers)
    {
        var isGet = HttpMethods.IsGet(method);
        var isHead = HttpMethods.IsHead(method);
        var isPut = HttpMethods.IsPut(method);
        var isDelete = HttpMethods.IsDelete(method);

        // Service level
        if (string.IsNullOrEmpty(container))
        {
            return (isGet, isPut, restype.ToLowerInvariant(), comp.ToLowerInvariant()) switch
            {
                (true, false, "", "list") => AzureOperationKind.ListContainers,
                (true, false, "service", "properties") => AzureOperationKind.GetServiceProperties,
                (false, true, "service", "properties") => AzureOperationKind.SetServiceProperties,
                (true, false, "account", "properties") => AzureOperationKind.GetAccountInfo,
                _ => AzureOperationKind.Unknown
            };
        }

        // Container level
        if (string.IsNullOrEmpty(blob))
        {
            return (isGet, isHead, isPut, isDelete, restype.ToLowerInvariant(), comp.ToLowerInvariant()) switch
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
        }

        // Blob level
        return isPut
            ? comp.ToLowerInvariant() switch
            {
                "block" => AzureOperationKind.PutBlock,
                "blocklist" => AzureOperationKind.PutBlockList,
                "metadata" => AzureOperationKind.SetBlobMetadata,
                "tags" => AzureOperationKind.PutBlobTags,
                _ => headers.ContainsKey("x-ms-copy-source") ? AzureOperationKind.CopyBlob : AzureOperationKind.PutBlob
            }
            : isGet
                ? comp.ToLowerInvariant() switch
                {
                    "blocklist" => AzureOperationKind.GetBlockList,
                    "metadata" => AzureOperationKind.GetBlobMetadata,
                    "tags" => AzureOperationKind.GetBlobTags,
                    _ => AzureOperationKind.GetBlob
                }
                : isHead
                    ? AzureOperationKind.HeadBlob
                    : isDelete
                        ? AzureOperationKind.DeleteBlob
                        : AzureOperationKind.Unknown;
    }
}
