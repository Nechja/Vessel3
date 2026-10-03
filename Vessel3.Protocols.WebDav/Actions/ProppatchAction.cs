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
        res.ContentType = WebDavMediaTypes.XmlUtf8;
        res.Headers[WebDavHeaders.Dav] = WebDavHeaders.DavComplianceLevel;
        res.Headers[WebDavHeaders.MsAuthorVia] = WebDavHeaders.DavAuthorValue;

        await xml.WriteProppatchResponse(res.Body, rawPath, ctx.RequestAborted);
        return Results.Empty;
    }
}
