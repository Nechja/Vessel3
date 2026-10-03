using System.Xml;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Headers;
using Vessel3.Protocols.Azure.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Actions.Blob;

internal sealed class PutBlockListAction(
    IChunkStager stager,
    IAzureErrorXmlWriter errorXml) : IAzureAction
{
    public AzureOperationKind Operation => AzureOperationKind.PutBlockList;

    public async Task<IResult> ExecuteAsync(AzureRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Container) || string.IsNullOrEmpty(target.Blob))
        {
            return new AzureErrorResult(new InvalidResourceNameError("Container and Blob are required"), errorXml);
        }

        var session = stager.ListSessions(target.Container)
            .FirstOrDefault(s => string.Equals(s.Key, target.Blob, StringComparison.Ordinal));

        if (session is null)
        {
            return new AzureErrorResult(new InvalidResourceNameError("No staged blocks found for blob"), errorXml);
        }

        using var bodyMs = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(bodyMs, ctx.RequestAborted);
        bodyMs.Position = 0;

        var blockIds = new List<string>();
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(bodyMs);
            if (doc.Root is not null)
            {
                foreach (var el in doc.Root.Elements())
                {
                    if (el.Name.LocalName is "Latest" or "Uncommitted" or "Committed")
                    {
                        var id = el.Value.Trim();
                        if (!string.IsNullOrEmpty(id))
                        {
                            blockIds.Add(id);
                        }
                    }
                }
            }
        }
        catch (XmlException)
        {
            return new AzureErrorResult(new InvalidResourceNameError("Malformed BlockList XML"), errorXml);
        }

        var chunksRes = stager.ListChunks(session.SessionId);
        if (!chunksRes.TryGetValue(out var stagedChunks, out var chunksErr))
        {
            return new AzureErrorResult(chunksErr, errorXml);
        }

        var chunkMap = stagedChunks.ToDictionary(c => c.Token, StringComparer.Ordinal);
        var orderedParts = new List<MultipartPart>(blockIds.Count);

        for (var i = 0; i < blockIds.Count; i++)
        {
            var id = blockIds[i];
            if (!chunkMap.TryGetValue(id, out var chunk))
            {
                return new AzureErrorResult(new InvalidResourceNameError($"Block '{id}' was not found"), errorXml);
            }
            orderedParts.Add(new MultipartPart(i + 1, chunk.BlobSha, chunk.Md5, chunk.Size));
        }

        var wireEtag = $"{Guid.NewGuid():N}";
        var metadata = AzureHeaderCodec.ExtractUserMetadata(ctx.Request.Headers);
        var contentType = ctx.Request.Headers["x-ms-blob-content-type"].ToString();

        var commitRes = await stager.Commit(
            session.SessionId,
            orderedParts,
            wireEtag,
            ChecksumSet.Empty,
            ctx.RequestAborted,
            metadataOverride: metadata.Count > 0 ? metadata : null,
            contentTypeOverride: !string.IsNullOrEmpty(contentType) ? contentType : null);

        if (!commitRes.TryGetValue(out _, out var commitErr))
        {
            return new AzureErrorResult(commitErr, errorXml);
        }

        ctx.Response.Headers.ETag = $"\"{wireEtag}\"";
        ctx.Response.Headers.LastModified = AzureXmlDefaults.ToRfc1123(DateTimeOffset.UtcNow);

        return Results.StatusCode(StatusCodes.Status201Created);
    }
}
