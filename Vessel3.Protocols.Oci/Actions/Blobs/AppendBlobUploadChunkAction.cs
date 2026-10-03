using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Oci.Dispatch;
using Vessel3.Protocols.Oci.Serialization;

namespace Vessel3.Protocols.Oci.Actions.Blobs;

internal sealed class AppendBlobUploadChunkAction(IContainerRepoCatalog catalog) : IOciAction
{
    public OciOperationKind Operation => OciOperationKind.AppendBlobUploadChunk;

    public async Task Execute(OciRequestTarget target, HttpContext ctx)
    {
        var repo = target.Repo ?? "";
        var uploadId = target.UploadId ?? "";

        var sessionResult = catalog.GetUploadSession(uploadId);
        if (!sessionResult.TryGetValue(out var session, out var err))
        {
            await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status404NotFound, OciErrorCodes.BlobUploadUnknown, err.Message);
            return;
        }

        await using (var fileStream = new FileStream(session.TempFilePath, FileMode.Append, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await ctx.Request.Body.CopyToAsync(fileStream, ctx.RequestAborted);
        }

        var fileInfo = new FileInfo(session.TempFilePath);
        var totalBytes = fileInfo.Length;
        catalog.UpdateUploadSession(uploadId, totalBytes);

        ctx.Response.StatusCode = StatusCodes.Status202Accepted;
        ctx.Response.Headers.Append(OciHeaders.Location, $"/v2/{repo}/blobs/uploads/{uploadId}");
        ctx.Response.Headers.Append(OciHeaders.Range, $"0-{Math.Max(0, totalBytes - 1)}");
        ctx.Response.Headers.Append(OciHeaders.DockerUploadUuid, uploadId);
    }
}
