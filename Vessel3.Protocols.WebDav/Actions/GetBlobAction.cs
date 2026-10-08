using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Vessel3.Primitives;
using Vessel3.Protocols.WebDav.Dispatch;
using Vessel3.Protocols.WebDav.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav.Actions;

internal sealed class GetBlobAction(IObjectStore objects) : IWebDavAction
{
    public WebDavOperationKind Operation => WebDavOperationKind.Get;

    public async Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Bucket))
        {
            return new WebDavErrorResult(new InvalidRequestError("Bucket is required for GET"));
        }

        if (string.IsNullOrEmpty(target.Path))
        {
            return new WebDavErrorResult(new MethodNotAllowedError("Cannot GET a bucket collection directly"));
        }

        var cleanPath = target.Path.TrimStart('/');
        var getRes = await objects.Get(target.Bucket, cleanPath, ct: ctx.RequestAborted);
        if (!getRes.TryGetValue(out var obj, out var err))
        {
            return new WebDavErrorResult(err);
        }

        var res = ctx.Response;
        res.Headers[WebDavHeaders.Dav] = WebDavHeaders.DavComplianceLevel;
        res.Headers[WebDavHeaders.MsAuthorVia] = WebDavHeaders.DavAuthorValue;

        var etagHeader = obj.Etag.StartsWith('"') ? obj.Etag : $"\"{obj.Etag}\"";

        return Results.Stream(
            obj.Body,
            contentType: obj.ContentType,
            lastModified: obj.LastModified,
            entityTag: new EntityTagHeaderValue(etagHeader),
            enableRangeProcessing: true);
    }
}
