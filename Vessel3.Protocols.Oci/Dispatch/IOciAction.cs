using Microsoft.AspNetCore.Http;

namespace Vessel3.Protocols.Oci.Dispatch;

internal interface IOciAction
{
    OciOperationKind Operation { get; }
    Task Execute(OciRequestTarget target, HttpContext ctx);
}
