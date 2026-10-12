using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Actions.Blob;

internal sealed class PutBlockAction(
    IChunkStager stager,
    IAzureErrorXmlWriter errorXml) : IAzureAction
{
    public AzureOperationKind Operation => AzureOperationKind.PutBlock;

    public async Task<IResult> Execute(AzureRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Container) || string.IsNullOrEmpty(target.Blob))
        {
            return new AzureErrorResult(new InvalidResourceNameError("Container and Blob are required"), errorXml);
        }

        if (!ctx.Request.Query.TryGetValue("blockid", out var bVal) || bVal.Count == 0 || bVal[0] is not { Length: > 0 } blockId)
        {
            return new AzureErrorResult(new InvalidResourceNameError("Missing blockid query parameter"), errorXml);
        }

        var session = stager.ListSessions(target.Container)
            .FirstOrDefault(s => string.Equals(s.Key, target.Blob, StringComparison.Ordinal));

        if (session is null)
        {
            var createRes = stager.CreateSession(target.Container, target.Blob, null, FrozenDictionary<string, string>.Empty);
            if (!createRes.TryGetValue(out session, out var createErr))
            {
                return new AzureErrorResult(createErr, errorXml);
            }
        }

        var stageRes = await stager.StageChunk(
            session.SessionId,
            blockId,
            ctx.Request.Body,
            ctx.Request.ContentLength,
            DeclaredChecksums.Empty,
            ctx.RequestAborted);

        if (!stageRes.TryGetValue(out var chunk, out var stageErr))
        {
            return new AzureErrorResult(stageErr, errorXml);
        }

        if (!string.IsNullOrEmpty(chunk.Md5))
        {
            try
            {
                ctx.Response.Headers["Content-MD5"] = Convert.ToBase64String(Convert.FromHexString(chunk.Md5));
            }
            catch (FormatException)
            {
            }
        }

        return Results.StatusCode(StatusCodes.Status201Created);
    }
}
