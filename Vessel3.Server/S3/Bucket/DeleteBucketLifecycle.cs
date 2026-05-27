namespace Vessel3.Server.S3.Bucket;

internal sealed class DeleteBucketLifecycle(IBucketRegistry registry, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Delete, S3BucketSubresource.Lifecycle);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        Task.FromResult(registry.RemoveLifecycle(bucket).Match<IResult>(() => Results.NoContent(), http.Map));
}
