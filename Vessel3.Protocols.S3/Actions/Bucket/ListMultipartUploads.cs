namespace Vessel3.Server.S3.Bucket;

internal sealed class ListMultipartUploads(IBucketRegistry registry, IMultipartStore multipart, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.Uploads);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        var existsRes = registry.Exists(bucket);
        if (!existsRes.TryGetValue(out var exists, out var err))
        {
            return http.Map(err);
        }

        if (!exists)
        {
            return http.Map(new NoSuchBucketError(bucket));
        }

        ctx.Response.ContentType = "application/xml";
        await xml.WriteListMultipartUploads(ctx.Response.Body, bucket, multipart.ListUploads(bucket), ctx.RequestAborted);
        return Results.Empty;
    }
}
