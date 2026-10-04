using System.Xml;
using Microsoft.AspNetCore.Http;
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

    public async Task<IResult> Execute(AzureRequestTarget target, HttpContext context)
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

        using var bodyStream = new MemoryStream();
        await context.Request.Body.CopyToAsync(bodyStream, context.RequestAborted);
        bodyStream.Position = 0;

        if (!TryParseBlockListXml(bodyStream, out var blockIds))
        {
            return new AzureErrorResult(new InvalidResourceNameError("Malformed BlockList XML"), errorXml);
        }

        var listChunksResult = stager.ListChunks(session.SessionId);
        if (!listChunksResult.TryGetValue(out var stagedChunks, out var chunksError))
        {
            return new AzureErrorResult(chunksError, errorXml);
        }

        var chunkMap = stagedChunks.ToDictionary(chunk => chunk.Token, StringComparer.Ordinal);
        if (!TryBuildOrderedParts(blockIds, chunkMap, out var orderedParts, out var missingBlockId))
        {
            return new AzureErrorResult(new InvalidResourceNameError($"Block '{missingBlockId}' was not found"), errorXml);
        }

        var wireEtag = $"{Guid.NewGuid():N}";
        var metadata = AzureHeaderCodec.ExtractUserMetadata(context.Request.Headers);
        var contentType = context.Request.Headers["x-ms-blob-content-type"].ToString();

        var commitResult = await stager.Commit(
            session.SessionId,
            orderedParts,
            wireEtag,
            ChecksumSet.Empty,
            context.RequestAborted,
            metadataOverride: metadata.Count > 0 ? metadata : null,
            contentTypeOverride: !string.IsNullOrEmpty(contentType) ? contentType : null);

        if (!commitResult.TryGetValue(out _, out var commitError))
        {
            return new AzureErrorResult(commitError, errorXml);
        }

        context.Response.Headers.ETag = $"\"{wireEtag}\"";
        context.Response.Headers.LastModified = AzureXmlDefaults.ToRfc1123(DateTimeOffset.UtcNow);

        return Results.StatusCode(StatusCodes.Status201Created);
    }

    private static bool TryParseBlockListXml(Stream stream, out List<string> blockIds)
    {
        blockIds = [];
        try
        {
            var document = System.Xml.Linq.XDocument.Load(stream);
            if (document.Root is not null)
            {
                foreach (var element in document.Root.Elements())
                {
                    if (element.Name.LocalName is "Latest" or "Uncommitted" or "Committed")
                    {
                        var blockId = element.Value.Trim();
                        if (!string.IsNullOrEmpty(blockId))
                        {
                            blockIds.Add(blockId);
                        }
                    }
                }
            }
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool TryBuildOrderedParts(
        IReadOnlyList<string> blockIds,
        IReadOnlyDictionary<string, StagedChunk> chunkMap,
        out List<MultipartPart> orderedParts,
        out string? missingBlockId)
    {
        orderedParts = new List<MultipartPart>(blockIds.Count);
        missingBlockId = null;

        for (var i = 0; i < blockIds.Count; i++)
        {
            var blockId = blockIds[i];
            if (!chunkMap.TryGetValue(blockId, out var chunk))
            {
                missingBlockId = blockId;
                return false;
            }
            orderedParts.Add(new MultipartPart(i + 1, chunk.BlobSha, chunk.Md5, chunk.Size));
        }

        return true;
    }
}
