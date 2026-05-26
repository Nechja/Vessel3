namespace Vessel3.Server.S3.Bucket;

internal sealed class DeleteBucketLifecycle(IBucketRegistry registry, IHttpResultMapper http) : IS3Action
{
    public S3Route Route => new(HttpMethods.Delete, S3Subresource.Lifecycle);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        Task.FromResult(registry.RemoveLifecycle(bucket).Match<IResult>(_ => Results.NoContent(), http.Map));
}
