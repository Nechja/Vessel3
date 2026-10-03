using Microsoft.AspNetCore.Http;

namespace Vessel3.Protocols.Azure.Dispatch;

internal interface IAzureAction
{
    AzureOperationKind Operation { get; }
    Task<IResult> Execute(AzureRequestTarget target, HttpContext ctx);
}
