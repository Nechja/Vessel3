using Microsoft.AspNetCore.Http;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Dispatch;

internal interface IAzureAction
{
    AzureOperationKind Operation { get; }
    BucketCapability RequiredCapability => BucketCapability.Write;
    Task<IResult> Execute(AzureRequestTarget target, HttpContext ctx);
}
