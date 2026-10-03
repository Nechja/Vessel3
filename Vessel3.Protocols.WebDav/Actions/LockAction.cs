using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.WebDav.Dispatch;
using Vessel3.Protocols.WebDav.Serialization;

namespace Vessel3.Protocols.WebDav.Actions;

internal sealed class LockAction(IWebDavXmlWriter xml) : IWebDavAction
{
    public WebDavOperationKind Operation => WebDavOperationKind.Lock;

    public async Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx)
    {
        var rawPath = ctx.Request.Path.Value ?? "/";
        var lockToken = $"urn:uuid:{Guid.NewGuid():D}";

        var res = ctx.Response;
        res.StatusCode = StatusCodes.Status200OK;
        res.ContentType = "application/xml; charset=utf-8";
        res.Headers["DAV"] = "1, 2";
        res.Headers["MS-Author-Via"] = "DAV";
        res.Headers["Lock-Token"] = $"<{lockToken}>";

        await xml.WriteLockDiscovery(res.Body, rawPath, lockToken, null, ctx.RequestAborted);
        return Results.Empty;
    }
}
