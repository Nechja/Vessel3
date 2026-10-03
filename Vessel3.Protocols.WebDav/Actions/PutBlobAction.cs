using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.WebDav.Dispatch;
using Vessel3.Protocols.WebDav.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav.Actions;

internal sealed class PutBlobAction(IObjectStore objects) : IWebDavAction
{
    public WebDavOperationKind Operation => WebDavOperationKind.Put;

    public async Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Bucket))
        {
            return new WebDavErrorResult(new InvalidRequestError("Bucket is required for PUT"));
        }

        if (string.IsNullOrEmpty(target.Path))
        {
            return new WebDavErrorResult(new InvalidRequestError("Key path is required for PUT"));
        }

        var cleanPath = target.Path.TrimStart('/');
        var isOverwrite = objects.Stat(target.Bucket, cleanPath) is Result<ObjectStat>.Success;

        var req = ctx.Request;
        var contentType = req.ContentType ?? "application/octet-stream";

        var putReq = new ObjectPutRequest(
            Bucket: target.Bucket,
            Key: cleanPath,
            Body: req.Body,
            DeclaredSize: req.ContentLength,
            ContentType: contentType,
            DeclaredSha256: null,
            DeclaredMd5Base64: null,
            Metadata: new Dictionary<string, string>(),
            Tags: new Dictionary<string, string>(),
            DeclaredChecksums: ChecksumSet.Empty,
            Ct: ctx.RequestAborted);

        var putRes = await objects.Put(putReq);
        if (!putRes.TryGetValue(out var put, out var err))
        {
            return new WebDavErrorResult(err);
        }

        var res = ctx.Response;
        res.Headers.ETag = put.Etag.StartsWith('"') ? put.Etag : $"\"{put.Etag}\"";
        res.Headers["DAV"] = "1, 2";
        res.Headers["MS-Author-Via"] = "DAV";

        return Results.StatusCode(isOverwrite ? StatusCodes.Status204NoContent : StatusCodes.Status201Created);
    }
}
