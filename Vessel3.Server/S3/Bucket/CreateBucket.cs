namespace Vessel3.Server.S3.Bucket;

internal sealed class CreateBucket(IBucketRegistry registry, IHttpResultMapper http) : IS3Action
{
    public S3Route Route => new(HttpMethods.Put, S3Subresource.None);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        Task.FromResult(registry.Create(bucket).Match<IResult>(_ => Results.Ok(), http.Map));
}
