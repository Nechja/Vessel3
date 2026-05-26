namespace Vessel3.Server.S3.Bucket;

internal sealed class PutBucketLifecycleConfiguration(IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Put, S3BucketSubresource.Lifecycle);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        var parsed = await reader.ReadLifecycleConfiguration(ctx.Request.Body, ctx.RequestAborted);
        if (parsed is Result<LifecycleConfig>.Failure pf) return http.Map(pf.Error);
        var cfg = ((Result<LifecycleConfig>.Success)parsed).Value;
        return registry.SetLifecycle(bucket, cfg).Match<IResult>(_ => Results.Ok(), http.Map);
    }
}
