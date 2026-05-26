namespace Vessel3.Server.S3.Bucket;

internal sealed class DeleteBucket(IBucketRegistry registry, IHttpResultMapper http) : IS3Action
{
    public S3Route Route => new(HttpMethods.Delete, S3Subresource.None);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        Task.FromResult(registry.Delete(bucket).Match<IResult>(_ => Results.NoContent(), http.Map));
}
