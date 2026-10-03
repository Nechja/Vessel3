using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.WebDav.Dispatch;
using Vessel3.Protocols.WebDav.Serialization;

namespace Vessel3.Protocols.WebDav.Actions;

internal sealed class ProppatchAction(IWebDavXmlWriter xml) : IWebDavAction
{
    public WebDavOperationKind Operation => WebDavOperationKind.Proppatch;

    public async Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx)
    {
        var rawPath = ctx.Request.Path.Value ?? "/";

        var res = ctx.Response;
        res.StatusCode = 207;
        res.ContentType = "application/xml; charset=utf-8";
        res.Headers["DAV"] = "1, 2";
        res.Headers["MS-Author-Via"] = "DAV";

        await xml.WriteProppatchResponse(res.Body, rawPath, ctx.RequestAborted);
        return Results.Empty;
    }
}
