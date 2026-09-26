namespace Vessel3.Server.S3.Bucket;

internal sealed class ListObjects(IBucketLister lister, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.None);

    public Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        var (listReq, continuationToken) = ListObjectsQuery.Bind(bucket, ctx.Request.Query);

        return lister.List(listReq, continuationToken).Match<Task<IResult>>(
            async page =>
            {
                ctx.Response.ContentType = "application/xml";
                await xml.WriteListObjects(ctx.Response.Body, listReq, page, ctx.RequestAborted);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
    }
}
