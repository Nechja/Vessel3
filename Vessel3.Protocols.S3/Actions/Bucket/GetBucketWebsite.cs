
namespace Vessel3.Server.S3.Bucket;

internal sealed class GetBucketWebsite(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.Website);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        registry.GetWebsite(bucket).Match<Task<IResult>>(
            async cfg =>
            {
                if (cfg is null) return http.Map(new NoSuchWebsiteConfigurationError(bucket));
                ctx.Response.ContentType = "application/xml";
                await xml.WriteWebsiteConfiguration(ctx.Response.Body, cfg, ctx.RequestAborted);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
}
