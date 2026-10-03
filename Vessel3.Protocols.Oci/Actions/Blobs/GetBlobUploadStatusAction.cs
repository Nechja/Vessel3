using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Oci.Dispatch;
using Vessel3.Protocols.Oci.Serialization;

namespace Vessel3.Protocols.Oci.Actions.Blobs;

internal sealed class GetBlobUploadStatusAction(IContainerRepoCatalog catalog) : IOciAction
{
    public OciOperationKind Operation => OciOperationKind.GetBlobUploadStatus;

    public async Task Execute(OciRequestTarget target, HttpContext ctx)
    {
        var uploadId = target.UploadId ?? "";
        var sessionResult = catalog.GetUploadSession(uploadId);
        if (!sessionResult.TryGetValue(out var session, out var err))
        {
            await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status404NotFound, OciErrorCodes.BlobUploadUnknown, err.Message);
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        ctx.Response.Headers.Append(OciHeaders.Range, $"0-{Math.Max(0, session.BytesReceived - 1)}");
        ctx.Response.Headers.Append(OciHeaders.DockerUploadUuid, uploadId);
    }
}
