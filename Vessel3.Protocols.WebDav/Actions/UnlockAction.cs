using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.WebDav.Dispatch;

namespace Vessel3.Protocols.WebDav.Actions;

internal sealed class UnlockAction : IWebDavAction
{
    public WebDavOperationKind Operation => WebDavOperationKind.Unlock;

    public Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx)
    {
        var res = ctx.Response;
        res.Headers["DAV"] = "1, 2";
        res.Headers["MS-Author-Via"] = "DAV";
        return Task.FromResult<IResult>(Results.NoContent());
    }
}
