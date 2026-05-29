namespace Vessel3.Server.S3.Bucket;

internal sealed class GetBucketLifecycleConfiguration(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.Lifecycle);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        registry.GetLifecycle(bucket).Match<Task<IResult>>(
            async cfg =>
            {
                if (cfg is null) return http.Map(new NoSuchLifecycleConfigurationError(bucket));
                ctx.Response.ContentType = "application/xml";
                await xml.WriteLifecycleConfiguration(ctx.Response.Body, cfg, ctx.RequestAborted);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
}
