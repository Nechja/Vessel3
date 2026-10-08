using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Oci.Dispatch;
using Vessel3.Protocols.Oci.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Oci.Actions.Blobs;

internal sealed class StartBlobUploadAction(
    IContainerRepoCatalog catalog,
    IBlobPool blobs) : IOciAction
{
    public OciOperationKind Operation => OciOperationKind.StartBlobUpload;

    public async Task Execute(OciRequestTarget target, HttpContext ctx)
    {
        var repo = target.Repo ?? "";
        var declaredDigest = ctx.Request.Query["digest"].ToString();

        if (!string.IsNullOrEmpty(declaredDigest))
        {
            await SingleShotUpload(repo, declaredDigest, ctx);
            return;
        }

        var sessionResult = catalog.StartUploadSession(repo);
        if (!sessionResult.TryGetValue(out var session, out var sessionErr))
        {
            await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status400BadRequest, OciErrorCodes.BlobUploadInvalid, sessionErr.Message);
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status202Accepted;
        ctx.Response.Headers.Append(OciHeaders.Location, $"/v2/{repo}/blobs/uploads/{session.Id}");
        ctx.Response.Headers.Append(OciHeaders.Range, "0-0");
        ctx.Response.Headers.Append(OciHeaders.DockerUploadUuid, session.Id);
    }

    private async Task SingleShotUpload(string repo, string declaredDigest, HttpContext ctx)
    {
        var cleanDeclaredSha = OciResponseWriter.CleanSha(declaredDigest);
        var writeResult = await blobs.Write(ctx.Request.Body, ctx.Request.ContentLength, ChecksumIntent.None, ctx.RequestAborted);
        if (!writeResult.TryGetValue(out var storedBlob, out var writeErr))
        {
            await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status400BadRequest, OciErrorCodes.BlobUploadInvalid, writeErr.Message);
            return;
        }

        if (!string.Equals(storedBlob.Sha, cleanDeclaredSha, StringComparison.OrdinalIgnoreCase))
        {
            await blobs.Delete(storedBlob.Sha, ctx.RequestAborted);
            await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status400BadRequest, OciErrorCodes.DigestInvalid, $"Declared digest {declaredDigest} did not match actual sha256:{storedBlob.Sha}");
            return;
        }

        catalog.GetOrCreateRepo(repo);
        ctx.Response.StatusCode = StatusCodes.Status201Created;
        ctx.Response.Headers.Append(OciHeaders.Location, $"/v2/{repo}/blobs/sha256:{storedBlob.Sha}");
        ctx.Response.Headers.Append(OciHeaders.DockerContentDigest, $"sha256:{storedBlob.Sha}");
    }
}
