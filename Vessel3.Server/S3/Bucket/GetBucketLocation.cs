namespace Vessel3.Server.S3.Bucket;

internal sealed class GetBucketLocation(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http, ServerRegion region) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.Location);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        registry.Exists(bucket).Match<Task<IResult>>(
            async exists =>
            {
                if (!exists) return http.Map(new NoSuchBucketError(bucket));
                ctx.Response.ContentType = "application/xml";
                await xml.WriteLocationConstraint(ctx.Response.Body, region.Value, ctx.RequestAborted);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
}
