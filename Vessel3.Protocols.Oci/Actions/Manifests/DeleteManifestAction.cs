using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Oci.Dispatch;
using Vessel3.Protocols.Oci.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Oci.Actions.Manifests;

internal sealed class DeleteManifestAction(
    IContainerRepoCatalog catalog,
    IWebhookEventPublisher? publisher = null) : IOciAction
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

        if (deleted)
        {
            var actor = ctx.Items.TryGetValue("CallerIdentity", out var c) && c is CallerIdentity ci ? ci.Username : "anonymous";
            publisher?.Publish(VesselEvents.ContainerImageDeleted(repo, reference, actor, ctx.Request.Host.Value));
        }

        ctx.Response.StatusCode = deleted ? StatusCodes.Status202Accepted : StatusCodes.Status404NotFound;
    }
}
