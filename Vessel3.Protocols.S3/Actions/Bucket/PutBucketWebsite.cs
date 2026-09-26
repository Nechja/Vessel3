
namespace Vessel3.Server.S3.Bucket;

internal sealed class PutBucketWebsite(IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Put, S3BucketSubresource.Website);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        !(await reader.ReadWebsiteConfiguration(ctx.Request.Body, ctx.RequestAborted)).TryGetValue(out var cfg, out var err)
            ? http.Map(err)
            : registry.SetWebsite(bucket, cfg).Match<IResult>(() => Results.Ok(), http.Map);
}
