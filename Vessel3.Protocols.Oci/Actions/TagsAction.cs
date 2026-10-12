using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Oci.Dispatch;
using Vessel3.Protocols.Oci.Serialization;

namespace Vessel3.Protocols.Oci.Actions;

internal sealed class TagsAction(IContainerRepoCatalog catalog) : IOciAction
{
    public OciOperationKind Operation => OciOperationKind.Tags;

    public async Task Execute(OciRequestTarget target, HttpContext ctx)
    {
        var repo = target.Repo ?? "";
        var limit = ctx.Request.Query.TryGetValue("n", out var nVal) && int.TryParse(nVal, out var parsedLimit) ? parsedLimit : 100;
        var last = ctx.Request.Query.TryGetValue("last", out var lastVal) && lastVal.Count > 0 && lastVal[0] is { Length: > 0 } l ? l : null;

        var tagsResult = catalog.ListTags(repo, limit, last);
        if (!tagsResult.TryGetValue(out var tags, out var err))
        {
            var code = err is NoSuchContainerRepoError ? OciErrorCodes.NameUnknown : OciErrorCodes.InternalError;
            var status = err is NoSuchContainerRepoError ? StatusCodes.Status404NotFound : StatusCodes.Status500InternalServerError;
            await OciResponseWriter.WriteOciError(ctx, status, code, err.Message);
            return;
        }

        if (tags.Count == limit && tags.Count > 0)
        {
            var nextLast = tags[^1];
            ctx.Response.Headers.Append(OciHeaders.Link, $"</v2/{repo}/tags/list?n={limit}&last={Uri.EscapeDataString(nextLast)}>; rel=\"next\"");
        }

        var dto = new TagsListDto(repo, tags);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = OciMediaTypes.Json;
        await JsonSerializer.SerializeAsync(ctx.Response.Body, dto, OciJsonContext.Default.TagsListDto, ctx.RequestAborted);
    }
}
