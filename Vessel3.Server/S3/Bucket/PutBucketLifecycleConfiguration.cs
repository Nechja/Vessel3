namespace Vessel3.Server.S3.Bucket;

internal sealed class PutBucketLifecycleConfiguration(IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Put, S3BucketSubresource.Lifecycle);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        return !(await reader.ReadLifecycleConfiguration(ctx.Request.Body, ctx.RequestAborted)).TryGetValue(out var cfg, out var err)
            ? http.Map(err)
            : registry.SetLifecycle(bucket, cfg).Match<IResult>(() => Results.Ok(), http.Map);
    }
}
