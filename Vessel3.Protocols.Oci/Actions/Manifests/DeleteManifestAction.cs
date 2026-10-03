using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Oci.Dispatch;
using Vessel3.Protocols.Oci.Serialization;

namespace Vessel3.Protocols.Oci.Actions.Manifests;

internal sealed class DeleteManifestAction(IContainerRepoCatalog catalog) : IOciAction
{
    public OciOperationKind Operation => OciOperationKind.DeleteManifest;

    public async Task Execute(OciRequestTarget target, HttpContext ctx)
    {
        var repo = target.Repo ?? "";
        var reference = target.Reference ?? "";

        var delResult = catalog.DeleteManifest(repo, reference);
        if (!delResult.TryGetValue(out var deleted, out var err))
        {
            await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status404NotFound, OciErrorCodes.ManifestUnknown, err.Message);
            return;
        }

        ctx.Response.StatusCode = deleted ? StatusCodes.Status202Accepted : StatusCodes.Status404NotFound;
    }
}
