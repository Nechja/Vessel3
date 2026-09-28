namespace Vessel3.Server.S3.Bucket;

internal sealed class DeleteBucket(IBucketRegistry registry, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Delete, S3BucketSubresource.None);

    public Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        var caller = ctx.GetCaller();
        var result = caller is not null
            ? registry.Delete(bucket, caller)
            : registry.Delete(bucket);
        return Task.FromResult(result.Match<IResult>(() => Results.NoContent(), http.Map));
    }
}
