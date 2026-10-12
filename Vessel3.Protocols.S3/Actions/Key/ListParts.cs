namespace Vessel3.Server.S3.Key;

internal sealed class ListParts(IMultipartStore multipart, IS3XmlWriter xml, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Get, S3KeySubresource.UploadId);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        var res = multipart.ListParts(uploadId);
        if (!res.TryGetValue(out var parts, out var err))
        {
            return http.Map(err);
        }

        ctx.Response.ContentType = "application/xml";
        await xml.WriteListParts(ctx.Response.Body, bucket, key, uploadId, parts, ctx.RequestAborted);
        return Results.Empty;
    }
}
