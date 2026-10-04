using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Headers;
using Vessel3.Protocols.Azure.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Actions.Blob;

internal sealed class HeadBlobAction(
    IObjectStore objects,
    IAzureErrorXmlWriter errorXml) : IAzureAction
{
    public AzureOperationKind Operation => AzureOperationKind.HeadBlob;

    public Task<IResult> Execute(AzureRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Container) || string.IsNullOrEmpty(target.Blob))
        {
            return Task.FromResult<IResult>(new AzureErrorResult(new InvalidResourceNameError("Container and Blob are required"), errorXml));
        }

        var statResult = objects.Stat(target.Container, target.Blob);
        if (!statResult.TryGetValue(out var stat, out var err))
        {
            return Task.FromResult<IResult>(new AzureErrorResult(err, errorXml));
        }

        var res = ctx.Response;
        res.ContentType = stat.ContentType;
        res.ContentLength = stat.Size;
        res.Headers.ETag = stat.Etag;
        res.Headers.LastModified = AzureXmlDefaults.ToRfc1123(stat.LastModified);
        res.Headers["x-ms-blob-type"] = "BlockBlob";
        res.Headers["x-ms-lease-status"] = "unlocked";
        res.Headers["x-ms-lease-state"] = "available";
        res.Headers["x-ms-server-encrypted"] = "true";

        AzureHeaderCodec.ApplyUserMetadata(res.Headers, stat.Metadata);

        return Task.FromResult(Results.Ok());
    }
}
