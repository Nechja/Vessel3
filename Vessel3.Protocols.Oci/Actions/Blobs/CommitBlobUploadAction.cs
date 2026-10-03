using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Oci.Dispatch;
using Vessel3.Protocols.Oci.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Oci.Actions.Blobs;

internal sealed class CommitBlobUploadAction(
    IContainerRepoCatalog catalog,
    IBlobPool blobs) : IOciAction
{
    public OciOperationKind Operation => OciOperationKind.CommitBlobUpload;

    public async Task Execute(OciRequestTarget target, HttpContext ctx)
    {
        var repo = target.Repo ?? "";
        var uploadId = target.UploadId ?? "";
        var declaredDigest = ctx.Request.Query["digest"].ToString();

        if (string.IsNullOrEmpty(declaredDigest))
        {
            await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status400BadRequest, OciErrorCodes.DigestInvalid, "Missing digest query parameter on upload commit");
            return;
        }

        var sessionResult = catalog.GetUploadSession(uploadId);
        if (!sessionResult.TryGetValue(out var session, out var err))
        {
            await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status404NotFound, OciErrorCodes.BlobUploadUnknown, err.Message);
            return;
        }

        if (ctx.Request.ContentLength is > 0)
        {
            await using var fileStream = new FileStream(session.TempFilePath, FileMode.Append, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await ctx.Request.Body.CopyToAsync(fileStream, ctx.RequestAborted);
        }

        var cleanDeclaredSha = OciResponseWriter.CleanSha(declaredDigest);

        StoredBlob storedBlob;
        await using (var readFileStream = new FileStream(session.TempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            var writeResult = await blobs.Write(readFileStream, readFileStream.Length, ChecksumIntent.None, ctx.RequestAborted);
            if (!writeResult.TryGetValue(out var b, out var writeErr))
            {
                await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status400BadRequest, OciErrorCodes.BlobUploadInvalid, writeErr.Message);
                return;
            }
            storedBlob = b;
        }

        if (!string.Equals(storedBlob.Sha, cleanDeclaredSha, StringComparison.OrdinalIgnoreCase))
        {
            blobs.Delete(storedBlob.Sha);
            catalog.CancelUploadSession(uploadId);
            await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status400BadRequest, OciErrorCodes.DigestInvalid, $"Declared digest {declaredDigest} does not match computed sha256:{storedBlob.Sha}");
            return;
        }

        catalog.CompleteUploadSession(uploadId);
        try { File.Delete(session.TempFilePath); } catch { }

        ctx.Response.StatusCode = StatusCodes.Status201Created;
        ctx.Response.Headers.Append(OciHeaders.Location, $"/v2/{repo}/blobs/sha256:{storedBlob.Sha}");
        ctx.Response.Headers.Append(OciHeaders.DockerContentDigest, $"sha256:{storedBlob.Sha}");
    }
}
