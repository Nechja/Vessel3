using Microsoft.AspNetCore.Http;

namespace Vessel3.Protocols.WebDav.Dispatch;

internal interface IWebDavAction
{
    WebDavOperationKind Operation { get; }
    Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx);
}
