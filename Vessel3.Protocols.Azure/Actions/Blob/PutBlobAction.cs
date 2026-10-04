using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Headers;
using Vessel3.Protocols.Azure.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Actions.Blob;

internal sealed class PutBlobAction(
    IObjectStore objects,
    IAzureErrorXmlWriter errorXml) : IAzureAction
{
    public AzureOperationKind Operation => AzureOperationKind.PutBlob;

    public async Task<IResult> Execute(AzureRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Container) || string.IsNullOrEmpty(target.Blob))
        {
            return new AzureErrorResult(new InvalidResourceNameError("Container and Blob are required"), errorXml);
        }

        var req = ctx.Request;
        var res = ctx.Response;
        var ct = ctx.RequestAborted;

        var metadata = AzureHeaderCodec.ExtractUserMetadata(req.Headers);
        var contentType = req.ContentType ?? "application/octet-stream";
        var declaredMd5 = req.Headers["Content-MD5"].ToString();
        if (string.IsNullOrEmpty(declaredMd5))
        {
            declaredMd5 = req.Headers["x-ms-blob-content-md5"].ToString();
        }

        var actor = ctx.Items.TryGetValue("CallerIdentity", out var c) && c is CallerIdentity ci ? ci.Username : "anonymous";
        var putReq = new ObjectPutRequest(
            target.Container,
            target.Blob,
            req.Body,
            req.ContentLength,
            contentType,
            DeclaredSha256: null,
            DeclaredMd5Base64: string.IsNullOrEmpty(declaredMd5) ? null : declaredMd5,
            Metadata: metadata,
            Tags: FrozenDictionary<string, string>.Empty,
            DeclaredChecksums: ChecksumSet.Empty,
            Ct: ct,
            Protocol: "azure",
            Actor: actor,
            Host: req.Host.Value);

        var result = await objects.Put(putReq);
        if (!result.TryGetValue(out var put, out var err))
        {
            return new AzureErrorResult(err, errorXml);
        }

        res.Headers.ETag = put.Etag;
        res.Headers.LastModified = AzureXmlDefaults.ToRfc1123(DateTimeOffset.UtcNow);
        res.Headers["x-ms-request-server-encrypted"] = "true";

        return Results.StatusCode(StatusCodes.Status201Created);
    }
}
