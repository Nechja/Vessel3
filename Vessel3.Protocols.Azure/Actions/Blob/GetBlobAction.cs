using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Headers;
using Vessel3.Protocols.Azure.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Actions.Blob;

internal sealed class GetBlobAction(
    IObjectStore objects,
    IAzureErrorXmlWriter errorXml) : IAzureAction
{
    public AzureOperationKind Operation => AzureOperationKind.GetBlob;

    public async Task<IResult> ExecuteAsync(AzureRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Container) || string.IsNullOrEmpty(target.Blob))
        {
            return new AzureErrorResult(new InvalidResourceNameError("Container and Blob are required"), errorXml);
        }

        var getResult = objects.Get(target.Container, target.Blob);
        if (!getResult.TryGetValue(out var obj, out var err))
        {
            return new AzureErrorResult(err, errorXml);
        }

        var range = ctx.Request.Headers.Range.ToString();
        if (string.IsNullOrEmpty(range) && ctx.Request.Headers.TryGetValue("x-ms-range", out var msRange) && !string.IsNullOrEmpty(msRange))
        {
            ctx.Request.Headers.Range = msRange;
        }

        var res = ctx.Response;
        res.Headers["x-ms-blob-type"] = "BlockBlob";
        res.Headers["x-ms-lease-status"] = "unlocked";
        res.Headers["x-ms-lease-state"] = "available";
        res.Headers["x-ms-server-encrypted"] = "true";

        AzureHeaderCodec.ApplyUserMetadata(res.Headers, obj.Metadata);

        var etagHeader = obj.Etag.StartsWith('"') ? obj.Etag : $"\"{obj.Etag}\"";

        return Results.Stream(
            obj.Body,
            contentType: obj.ContentType,
            lastModified: obj.LastModified,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(etagHeader),
            enableRangeProcessing: true);
    }
}
