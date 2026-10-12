using System.Buffers;
using Microsoft.AspNetCore.Http;
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

    public async Task<IResult> Execute(AzureRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Container) || string.IsNullOrEmpty(target.Blob))
        {
            return new AzureErrorResult(new InvalidResourceNameError("Container and Blob are required"), errorXml);
        }

        var getResult = await objects.Get(target.Container, target.Blob, ct: ctx.RequestAborted);
        if (!getResult.TryGetValue(out var obj, out var err))
        {
            return new AzureErrorResult(err, errorXml);
        }

        var hasRange = ctx.Request.Headers.TryGetValue("Range", out var rangeVal) && !string.IsNullOrEmpty(rangeVal);
        if (!hasRange && ctx.Request.Headers.TryGetValue("x-ms-range", out var msRange) && !string.IsNullOrEmpty(msRange))
        {
            ctx.Request.Headers.Range = msRange;
            hasRange = true;
        }

        var res = ctx.Response;
        res.Headers["x-ms-blob-type"] = "BlockBlob";
        res.Headers["x-ms-lease-status"] = "unlocked";
        res.Headers["x-ms-lease-state"] = "available";
        res.Headers["x-ms-server-encrypted"] = "true";

        AzureHeaderCodec.ApplyUserMetadata(res.Headers, obj.Metadata);

        var etagHeader = obj.Etag.StartsWith('"') ? obj.Etag : $"\"{obj.Etag}\"";

        if (!hasRange)
        {
            res.ContentType = obj.ContentType;
            res.ContentLength = obj.Size;
            res.Headers.ETag = etagHeader;
            res.Headers.LastModified = AzureXmlDefaults.ToRfc1123(obj.LastModified);
            res.Headers.AcceptRanges = "bytes";

            await using (obj.Body)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(80 * 1024);
                try
                {
                    int bytesRead;
                    while ((bytesRead = await obj.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ctx.RequestAborted)) > 0)
                    {
                        await res.Body.WriteAsync(buffer.AsMemory(0, bytesRead), ctx.RequestAborted);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            return Results.Empty;
        }

        return Results.Stream(
            obj.Body,
            contentType: obj.ContentType,
            lastModified: obj.LastModified,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(etagHeader),
            enableRangeProcessing: true);
    }
}
