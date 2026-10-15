using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.WebDav.Dispatch;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav.Actions;

internal sealed class OptionsAction : IWebDavAction
{
    public WebDavOperationKind Operation => WebDavOperationKind.Options;
    public BucketCapability RequiredCapability => BucketCapability.Read;

    public Task<IResult> Execute(WebDavRequestTarget target, HttpContext ctx)
    {
        var res = ctx.Response;
        res.Headers[WebDavHeaders.Dav] = WebDavHeaders.DavComplianceLevel;
        res.Headers[WebDavHeaders.MsAuthorVia] = WebDavHeaders.DavAuthorValue;
        res.Headers[WebDavHeaders.Allow] = WebDavHeaders.AllowedMethods;
        res.Headers[WebDavHeaders.AcceptRanges] = WebDavHeaders.BytesUnit;

        return Task.FromResult<IResult>(Results.Ok());
    }
}
