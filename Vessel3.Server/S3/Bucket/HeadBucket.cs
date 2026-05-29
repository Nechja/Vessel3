namespace Vessel3.Server.S3.Bucket;

internal sealed class HeadBucket(IBucketRegistry registry, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Head, S3BucketSubresource.None);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        Task.FromResult(registry.Exists(bucket).Match<IResult>(
            exists => exists ? Results.Ok() : Results.NotFound(),
            http.Map));
}
