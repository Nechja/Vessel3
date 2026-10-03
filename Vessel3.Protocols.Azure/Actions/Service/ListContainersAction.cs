using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Actions.Service;

internal sealed class ListContainersAction(
    IBucketRegistry registry,
    IAzureXmlWriter xml) : IAzureAction
{
    public AzureOperationKind Operation => AzureOperationKind.ListContainers;

    public async Task<IResult> Execute(AzureRequestTarget target, HttpContext ctx)
    {
        var caller = ctx.Items.TryGetValue("CallerIdentity", out var c) && c is CallerIdentity ci
            ? ci
            : CallerIdentity.System;

        var buckets = registry.List(caller);

        var query = ctx.Request.Query;
        var prefix = query["prefix"].ToString();
        var marker = query["marker"].ToString();
        int? maxResults = int.TryParse(query["maxresults"].ToString(), out var m) ? m : null;

        if (!string.IsNullOrEmpty(prefix))
        {
            buckets = buckets.Where(b => b.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        var serviceEndpoint = $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}";
        if (!string.IsNullOrEmpty(target.Account))
        {
            serviceEndpoint += $"/{target.Account}";
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/xml";

        await xml.WriteListContainers(
            ctx.Response.Body,
            serviceEndpoint,
            string.IsNullOrEmpty(prefix) ? null : prefix,
            string.IsNullOrEmpty(marker) ? null : marker,
            maxResults,
            buckets,
            ctx.RequestAborted);

        return Results.Empty;
    }
}
