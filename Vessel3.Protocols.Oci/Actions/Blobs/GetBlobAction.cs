using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Oci.Dispatch;
using Vessel3.Protocols.Oci.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Oci.Actions.Blobs;

internal sealed class GetBlobAction(
    IBlobPool blobs,
    IOciResponseWriter? responseWriter = null) : IOciAction
{
    private readonly IOciResponseWriter responseWriter = responseWriter ?? OciResponseWriter.Instance;

    public OciOperationKind Operation => OciOperationKind.GetBlob;

    public async Task Execute(OciRequestTarget target, HttpContext ctx)
    {
        var digest = target.Reference ?? "";
        var cleanSha = responseWriter.CleanSha(digest);

        var openResult = await blobs.Open(cleanSha, ctx.RequestAborted);
        if (!openResult.TryGetValue(out var stream, out _))
        {
            await responseWriter.WriteOciError(ctx, StatusCodes.Status404NotFound, OciErrorCodes.BlobUnknown, $"Blob unknown: {digest}");
            return;
        }

        ctx.Response.Headers.Append(OciHeaders.DockerContentDigest, $"sha256:{cleanSha}");
        RequestTrace.Current?.MarkHandled();
        await Results.Stream(stream, contentType: OciMediaTypes.OctetStream, enableRangeProcessing: true).ExecuteAsync(ctx);
    }
}
