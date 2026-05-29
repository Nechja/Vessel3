using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3.Bucket;

internal sealed class ListObjects(IBucketLister lister, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.None);

    public Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var prefix = Nullify(q["prefix"].ToString());
        var delimiter = Nullify(q["delimiter"].ToString());
        var maxKeys = int.TryParse(q["max-keys"].ToString(), out var mk) ? mk : (int?)null;
        var continuationToken = Nullify(q["continuation-token"].ToString());
        var startAfter = Nullify(q["start-after"].ToString());
        var marker = Nullify(q["marker"].ToString());
        var listType = Nullify(q["list-type"].ToString());
        var encodingType = Nullify(q["encoding-type"].ToString());

        var isV1 = listType is not "2";
        var effectiveStart = isV1 ? marker : startAfter;
        var listReq = new ListRequest(
            bucket, prefix, delimiter, effectiveStart,
            Math.Clamp(maxKeys ?? 1000, 1, 1000),
            IsV1: isV1, Marker: marker, EncodingType: encodingType);

        return lister.List(listReq, isV1 ? null : continuationToken).Match<Task<IResult>>(
            async page =>
            {
                ctx.Response.ContentType = "application/xml";
                await xml.WriteListObjects(ctx.Response.Body, listReq, page, ctx.RequestAborted);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
    }
}
