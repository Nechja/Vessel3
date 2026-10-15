using Microsoft.AspNetCore.Http;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav.Dispatch;

internal interface IWebDavAction
{
    WebDavOperationKind Operation { get; }
    BucketCapability RequiredCapability => BucketCapability.Write;
    Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx);
}
