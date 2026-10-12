using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Azure.Serialization;

namespace Vessel3.Protocols.Azure.Headers;

internal static class AzureHeaderCodec
{
    public const string DefaultApiVersion = "2024-05-04";

    public static void ApplyStandardResponseHeaders(HttpContext ctx)
    {
        var res = ctx.Response;
        var req = ctx.Request;

        var requestId = ctx.Items.TryGetValue("AzureRequestId", out var r) && r is string reqId
            ? reqId
            : Guid.NewGuid().ToString();

        res.Headers["x-ms-request-id"] = requestId;

        var clientVersion = req.Headers.TryGetValue("x-ms-version", out var v) && v.Count > 0 && v[0] is { Length: > 0 } sv
            ? sv
            : DefaultApiVersion;
        res.Headers["x-ms-version"] = clientVersion;

        if (req.Headers.TryGetValue("x-ms-client-request-id", out var crId) && crId.Count > 0 && crId[0] is { Length: > 0 } reqIdStr)
        {
            res.Headers["x-ms-client-request-id"] = reqIdStr;
        }

        res.Headers["Date"] = AzureXmlDefaults.ToRfc1123(DateTimeOffset.UtcNow);
        res.Headers["Server"] = "Windows-Azure-Blob/1.0 Vessel3/1.0";
    }

    public static Dictionary<string, string> ExtractUserMetadata(IHeaderDictionary headers)
    {
        Dictionary<string, string> meta = new(StringComparer.OrdinalIgnoreCase);
        const string prefix = "x-ms-meta-";
        foreach (var (key, value) in headers)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.Length > prefix.Length)
            {
                var name = key[prefix.Length..];
                meta[name] = value.ToString();
            }
        }
        return meta;
    }

    public static void ApplyUserMetadata(IHeaderDictionary headers, IReadOnlyDictionary<string, string>? meta)
    {
        if (meta is null) return;
        foreach (var (k, v) in meta)
        {
            headers[$"x-ms-meta-{k}"] = v;
        }
    }
}
