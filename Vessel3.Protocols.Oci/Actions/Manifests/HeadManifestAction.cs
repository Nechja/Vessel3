using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Oci.Dispatch;
using Vessel3.Protocols.Oci.Serialization;

namespace Vessel3.Protocols.Oci.Actions.Manifests;

internal sealed class HeadManifestAction(IContainerRepoCatalog catalog) : IOciAction
{
    public OciOperationKind Operation => OciOperationKind.HeadManifest;

    public async Task Execute(OciRequestTarget target, HttpContext ctx)
    {
        var repo = target.Repo ?? "";
        var reference = target.Reference ?? "";

        var manifestResult = catalog.GetManifest(repo, reference);
        if (!manifestResult.TryGetValue(out var manifest, out var err))
        {
            await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status404NotFound, OciErrorCodes.ManifestUnknown, err.Message);
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = manifest.MediaType;
        ctx.Response.Headers.Append(OciHeaders.DockerContentDigest, manifest.Digest);
        ctx.Response.ContentLength = manifest.Size;
    }
}
