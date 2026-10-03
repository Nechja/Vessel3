using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Azure.Dispatch;

namespace Vessel3.Protocols.Azure.Actions.Service;

internal sealed class GetAccountInfoAction : IAzureAction
{
    public AzureOperationKind Operation => AzureOperationKind.GetAccountInfo;

    public Task<IResult> Execute(AzureRequestTarget target, HttpContext ctx)
    {
        ctx.Response.Headers["x-ms-sku-name"] = "Standard_LRS";
        ctx.Response.Headers["x-ms-account-kind"] = "StorageV2";
        ctx.Response.Headers["x-ms-is-hns-enabled"] = "false";
        return Task.FromResult(Results.Ok());
    }
}
