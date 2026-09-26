namespace Vessel3.Server.S3.Bucket;

internal sealed class DeleteBucketCors(IBucketRegistry registry, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Delete, S3BucketSubresource.Cors);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        Task.FromResult(registry.RemoveCors(bucket).Match<IResult>(() => Results.NoContent(), http.Map));
}
