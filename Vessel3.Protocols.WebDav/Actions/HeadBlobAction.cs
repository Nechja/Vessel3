using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.WebDav.Dispatch;
using Vessel3.Protocols.WebDav.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav.Actions;

internal sealed class HeadBlobAction(IObjectStore objects) : IWebDavAction
{
    public WebDavOperationKind Operation => WebDavOperationKind.Head;

    public Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx) =>
        Task.FromResult(ExecuteCore(target, ctx));

    private IResult ExecuteCore(WebDavRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Bucket))
        {
            return new WebDavErrorResult(new InvalidRequestError("Bucket is required for HEAD"));
        }

        if (string.IsNullOrEmpty(target.Path))
        {
            return new WebDavErrorResult(new MethodNotAllowedError("Cannot HEAD a bucket collection directly"));
        }

        var cleanPath = target.Path.TrimStart('/');
        var statRes = objects.Stat(target.Bucket, cleanPath);
        if (!statRes.TryGetValue(out var stat, out var err))
        {
            return new WebDavErrorResult(err);
        }

        var res = ctx.Response;
        res.Headers[WebDavHeaders.Dav] = WebDavHeaders.DavComplianceLevel;
        res.Headers[WebDavHeaders.MsAuthorVia] = WebDavHeaders.DavAuthorValue;
        res.ContentType = stat.ContentType;
        res.ContentLength = stat.Size;
        res.Headers.ETag = stat.Etag.StartsWith('"') ? stat.Etag : $"\"{stat.Etag}\"";
        res.Headers.LastModified = WebDavXmlDefaults.ToRfc1123(stat.LastModified);

        return Results.Ok();
    }
}
