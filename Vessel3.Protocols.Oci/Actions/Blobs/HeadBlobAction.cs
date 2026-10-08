using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Oci.Dispatch;
using Vessel3.Protocols.Oci.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Oci.Actions.Blobs;

internal sealed class HeadBlobAction(IBlobPool blobs) : IOciAction
{
    public OciOperationKind Operation => OciOperationKind.HeadBlob;

    public async Task Execute(OciRequestTarget target, HttpContext ctx)
    {
        var digest = target.Reference ?? "";
        var cleanSha = OciResponseWriter.CleanSha(digest);

        var openResult = await blobs.Open(cleanSha, ctx.RequestAborted);
        if (!openResult.TryGetValue(out var stream, out _))
        {
            await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status404NotFound, OciErrorCodes.BlobUnknown, $"Blob unknown: {digest}");
            return;
        }

        using (stream)
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = OciMediaTypes.OctetStream;
            ctx.Response.Headers.Append(OciHeaders.DockerContentDigest, $"sha256:{cleanSha}");
            ctx.Response.ContentLength = stream.Length;
        }
    }
}
