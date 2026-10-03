using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.WebDav.Dispatch;

namespace Vessel3.Protocols.WebDav.Actions;

internal sealed class OptionsAction : IWebDavAction
{
    public WebDavOperationKind Operation => WebDavOperationKind.Options;

    public Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx)
    {
        var res = ctx.Response;
        res.Headers["DAV"] = "1, 2";
        res.Headers["MS-Author-Via"] = "DAV";
        res.Headers["Allow"] = "OPTIONS, GET, HEAD, POST, PUT, DELETE, PROPFIND, PROPPATCH, MKCOL, COPY, MOVE, LOCK, UNLOCK";
        res.Headers["Accept-Ranges"] = "bytes";

        return Task.FromResult<IResult>(Results.Ok());
    }
}
