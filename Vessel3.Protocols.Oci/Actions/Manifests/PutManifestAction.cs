using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Oci.Dispatch;
using Vessel3.Protocols.Oci.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Oci.Actions.Manifests;

internal sealed class PutManifestAction(
    IContainerRepoCatalog catalog,
    IBlobPool blobs,
    IWebhookEventPublisher? publisher = null) : IOciAction
{
    public OciOperationKind Operation => OciOperationKind.PutManifest;

    public async Task Execute(OciRequestTarget target, HttpContext ctx)
    {
        var repo = target.Repo ?? "";
        var reference = target.Reference ?? "";

        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        var payload = ms.ToArray();

        if (payload.Length == 0)
        {
            await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status400BadRequest, OciErrorCodes.ManifestInvalid, "Manifest payload cannot be empty");
            return;
        }

        var mediaType = ctx.Request.ContentType ?? OciMediaTypes.ManifestV2;
        var layerDigests = ExtractReferencedDigests(payload);

        var putResult = catalog.PutManifest(repo, reference, mediaType, payload, layerDigests);
        if (!putResult.TryGetValue(out var outcome, out var putErr))
        {
            await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status400BadRequest, OciErrorCodes.ManifestInvalid, putErr.Message);
            return;
        }

        await blobs.Write(new MemoryStream(payload), payload.Length, ChecksumIntent.None, ctx.RequestAborted);

        ctx.Response.StatusCode = StatusCodes.Status201Created;
        ctx.Response.Headers.Append(OciHeaders.Location, $"/v2/{repo}/manifests/{outcome.Digest}");
        ctx.Response.Headers.Append(OciHeaders.DockerContentDigest, outcome.Digest);

        var actor = ctx.Items.TryGetValue("CallerIdentity", out var c) && c is CallerIdentity ci ? ci.Username : "anonymous";
        publisher?.Publish(VesselEvents.ContainerImagePushed(
            repo, reference, outcome.Digest, mediaType, payload.Length, actor, ctx.Request.Host.Value));
    }

    private static IReadOnlyList<string> ExtractReferencedDigests(byte[] payload)
    {
        List<string> list = [];
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("config", out var config) && config.TryGetProperty("digest", out var cfgDigest) && cfgDigest.GetString() is { } cd)
                list.Add(cd);

            AppendDigests(root, "layers", list);
            AppendDigests(root, "manifests", list);
        }
        catch
        {
        }
        return list;
    }

    private static void AppendDigests(JsonElement root, string propertyName, List<string> list)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array) return;
        foreach (var item in array.EnumerateArray())
        {
            if (item.TryGetProperty("digest", out var digest) && digest.GetString() is { } d)
                list.Add(d);
        }
    }
}
