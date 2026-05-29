namespace Vessel3.Server.S3.Bucket;

internal sealed class PutObjectLockConfiguration(IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Put, S3BucketSubresource.ObjectLock);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        return !(await reader.ReadObjectLockConfiguration(ctx.Request.Body, ctx.RequestAborted)).TryGetValue(out var cfg, out var err)
            ? http.Map(err)
            : registry.SetObjectLock(bucket, cfg).Match<IResult>(() => Results.Ok(), http.Map);
    }
}
