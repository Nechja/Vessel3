namespace Vessel3.Server.S3.Bucket;

internal sealed class GetObjectLockConfiguration(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.ObjectLock);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        registry.GetObjectLock(bucket).Match<Task<IResult>>(
            async cfg =>
            {
                if (cfg is null) return http.Map(new ObjectLockConfigurationNotFoundErrorResult(bucket));
                ctx.Response.ContentType = "application/xml";
                await xml.WriteObjectLockConfiguration(ctx.Response.Body, cfg, ctx.RequestAborted);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
}
