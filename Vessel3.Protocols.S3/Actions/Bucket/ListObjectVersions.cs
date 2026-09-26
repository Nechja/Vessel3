namespace Vessel3.Server.S3.Bucket;

internal sealed class ListObjectVersions(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.Versions);

    public Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        var q = ListObjectVersionsQuery.Bind(ctx.Request.Query);

        return registry.ListAllVersions(bucket, q.Prefix, q.KeyMarker, q.MaxKeys).Match<Task<IResult>>(
            async page =>
            {
                ctx.Response.ContentType = "application/xml";
                await xml.WriteListVersions(ctx.Response.Body, bucket, q.Prefix, page.Entries, page.IsTruncated, q.MaxKeys, q.EncodingType, ctx.RequestAborted);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
    }
}
