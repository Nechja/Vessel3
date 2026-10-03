using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Oci.Dispatch;
using Vessel3.Protocols.Oci.Serialization;

namespace Vessel3.Protocols.Oci.Actions;

internal sealed class CatalogAction(IContainerRepoCatalog catalog) : IOciAction
{
    public OciOperationKind Operation => OciOperationKind.Catalog;

    public async Task Execute(OciRequestTarget target, HttpContext ctx)
    {
        var nStr = ctx.Request.Query["n"].ToString();
        var limit = int.TryParse(nStr, out var parsedLimit) ? parsedLimit : 100;
        var last = ctx.Request.Query["last"].ToString();
        if (string.IsNullOrEmpty(last)) last = null;

        var listResult = catalog.ListRepos(limit, last);
        if (!listResult.TryGetValue(out var repos, out var err))
        {
            await OciResponseWriter.WriteOciError(ctx, StatusCodes.Status500InternalServerError, OciErrorCodes.InternalError, err.Message);
            return;
        }

        if (repos.Count == limit && repos.Count > 0)
        {
            var nextLast = repos[^1];
            ctx.Response.Headers.Append(OciHeaders.Link, $"</v2/_catalog?n={limit}&last={Uri.EscapeDataString(nextLast)}>; rel=\"next\"");
        }

        var dto = new CatalogListDto(repos);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = OciMediaTypes.Json;
        await JsonSerializer.SerializeAsync(ctx.Response.Body, dto, OciJsonContext.Default.CatalogListDto, ctx.RequestAborted);
    }
}
