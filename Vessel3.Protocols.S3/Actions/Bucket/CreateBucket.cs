namespace Vessel3.Server.S3.Bucket;

internal sealed class CreateBucket(IBucketRegistry registry, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Put, S3BucketSubresource.None);

    public Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        var caller = ctx.GetCaller();
        var result = caller is not null
            ? registry.Create(bucket, caller)
            : registry.Create(bucket);
        return Task.FromResult(result.Match<IResult>(_ => Results.Ok(), http.Map));
    }
}
