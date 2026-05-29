namespace Vessel3.Server.S3.Bucket;

internal sealed class DeleteBucket(IBucketRegistry registry, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Delete, S3BucketSubresource.None);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        Task.FromResult(registry.Delete(bucket).Match<IResult>(() => Results.NoContent(), http.Map));
}
