namespace Vessel3.Server.S3.Bucket;

internal sealed class ListMultipartUploads(IBucketRegistry registry, IMultipartStore multipart, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.Uploads);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        registry.Exists(bucket).Match<Task<IResult>>(
            async exists =>
            {
                if (!exists) return http.Map(new NoSuchBucketError(bucket));
                ctx.Response.ContentType = "application/xml";
                await xml.WriteListMultipartUploads(ctx.Response.Body, bucket, multipart.ListUploads(bucket), ctx.RequestAborted);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
}
