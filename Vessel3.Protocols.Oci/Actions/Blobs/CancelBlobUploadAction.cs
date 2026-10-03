using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Oci.Dispatch;

namespace Vessel3.Protocols.Oci.Actions.Blobs;

internal sealed class CancelBlobUploadAction(IContainerRepoCatalog catalog) : IOciAction
{
    public OciOperationKind Operation => OciOperationKind.CancelBlobUpload;

    public Task Execute(OciRequestTarget target, HttpContext ctx)
    {
        var uploadId = target.UploadId ?? "";
        catalog.CancelUploadSession(uploadId);
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }
}
