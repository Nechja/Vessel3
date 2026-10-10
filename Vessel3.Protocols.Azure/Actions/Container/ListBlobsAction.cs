using System.Globalization;
using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Actions.Container;

internal sealed class ListBlobsAction(
    IBucketLister lister,
    IAzureXmlWriter xml,
    IAzureErrorXmlWriter errorXml) : IAzureAction
{
    public AzureOperationKind Operation => AzureOperationKind.ListBlobs;

    public async Task<IResult> Execute(AzureRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Container))
        {
            return new AzureErrorResult(new InvalidResourceNameError("Container name is required"), errorXml);
        }

        var req = ctx.Request;
        var prefix = QueryParam(req.Query, "prefix");
        var delimiter = QueryParam(req.Query, "delimiter");
        var marker = QueryParam(req.Query, "marker");
        var maxResultsStr = QueryParam(req.Query, "maxresults");

        var maxKeys = 5000;
        if (maxResultsStr is not null && int.TryParse(maxResultsStr, CultureInfo.InvariantCulture, out var parsedMax) && parsedMax > 0)
        {
            maxKeys = Math.Min(parsedMax, 5000);
        }

        var listReq = new ListRequest(
            Bucket: target.Container,
            Prefix: prefix,
            Delimiter: delimiter,
            StartAfter: null,
            MaxKeys: maxKeys);

        var listResult = lister.List(listReq, marker);
        if (!listResult.TryGetValue(out var page, out var err))
        {
            return new AzureErrorResult(err, errorXml);
        }

        var serviceEndpoint = $"{req.Scheme}://{req.Host}/{target.Account ?? "devstoreaccount1"}";
        ctx.Response.ContentType = "application/xml";

        await xml.WriteListBlobs(
            ctx.Response.Body,
            serviceEndpoint,
            target.Container,
            prefix,
            marker,
            maxKeys,
            delimiter,
            page,
            ctx.RequestAborted);

        return Results.Empty;
    }

    private static string? QueryParam(IQueryCollection q, string k) =>
        q.TryGetValue(k, out var v) && v.Count > 0 && v[0] is { Length: > 0 } s ? s : null;
}
