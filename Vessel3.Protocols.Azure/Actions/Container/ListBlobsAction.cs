using System.Globalization;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
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

    public async Task<IResult> ExecuteAsync(AzureRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Container))
        {
            return new AzureErrorResult(new InvalidResourceNameError("Container name is required"), errorXml);
        }

        var req = ctx.Request;
        var prefix = req.Query["prefix"].ToString();
        var delimiter = req.Query["delimiter"].ToString();
        var marker = req.Query["marker"].ToString();
        var maxResultsStr = req.Query["maxresults"].ToString();

        var maxKeys = 5000;
        if (!string.IsNullOrEmpty(maxResultsStr) && int.TryParse(maxResultsStr, CultureInfo.InvariantCulture, out var parsedMax) && parsedMax > 0)
        {
            maxKeys = Math.Min(parsedMax, 5000);
        }

        var listReq = new ListRequest(
            Bucket: target.Container,
            Prefix: string.IsNullOrEmpty(prefix) ? null : prefix,
            Delimiter: string.IsNullOrEmpty(delimiter) ? null : delimiter,
            StartAfter: null,
            MaxKeys: maxKeys);

        var listResult = lister.List(listReq, string.IsNullOrEmpty(marker) ? null : marker);
        if (!listResult.TryGetValue(out var page, out var err))
        {
            return new AzureErrorResult(err, errorXml);
        }

        var serviceEndpoint = $"{req.Scheme}://{req.Host}/{target.Account ?? "devstoreaccount1"}";
        ctx.Response.ContentType = "application/xml";

        await xml.WriteListBlobsAsync(
            ctx.Response.Body,
            serviceEndpoint,
            target.Container,
            string.IsNullOrEmpty(prefix) ? null : prefix,
            string.IsNullOrEmpty(marker) ? null : marker,
            maxKeys,
            string.IsNullOrEmpty(delimiter) ? null : delimiter,
            page,
            ctx.RequestAborted);

        return Results.Empty;
    }
}
