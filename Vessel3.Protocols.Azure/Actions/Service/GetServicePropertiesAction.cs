using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Serialization;

namespace Vessel3.Protocols.Azure.Actions.Service;

internal sealed class GetServicePropertiesAction(IAzureXmlWriter xml) : IAzureAction
{
    public AzureOperationKind Operation => AzureOperationKind.GetServiceProperties;

    public async Task<IResult> ExecuteAsync(AzureRequestTarget target, HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/xml";
        await xml.WriteServicePropertiesAsync(ctx.Response.Body, ctx.RequestAborted);
        return Results.Empty;
    }
}
