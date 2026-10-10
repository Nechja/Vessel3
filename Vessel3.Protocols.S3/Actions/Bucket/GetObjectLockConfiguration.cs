namespace Vessel3.Server.S3.Bucket;

internal sealed class GetObjectLockConfiguration(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.ObjectLock);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        if (!registry.GetObjectLock(bucket).TryGetValue(out var cfg, out var err))
            return http.Map(err);

        if (cfg is null) return http.Map(new ObjectLockConfigurationNotFoundErrorResult(bucket));

        ctx.Response.ContentType = "application/xml";
        await xml.WriteObjectLockConfiguration(ctx.Response.Body, cfg, ctx.RequestAborted);
        return Results.Empty;
    }
}
