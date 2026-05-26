namespace Vessel3.Server.S3.Bucket;

internal sealed class CreateBucket(IBucketRegistry registry, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Put, S3BucketSubresource.None);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        Task.FromResult(registry.Create(bucket).Match<IResult>(_ => Results.Ok(), http.Map));
}
