using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Oci.Dispatch;

namespace Vessel3.Protocols.Oci.Actions;

internal sealed class PingAction : IOciAction
{
    public OciOperationKind Operation => OciOperationKind.Ping;

    public Task Execute(OciRequestTarget target, HttpContext ctx)
    {
        ctx.Response.Headers.Append(OciHeaders.DockerDistributionApiVersion, OciHeaders.ApiVersionValue);
        ctx.Response.ContentType = OciMediaTypes.Json;
        return ctx.Response.WriteAsync("{}", ctx.RequestAborted);
    }
}
