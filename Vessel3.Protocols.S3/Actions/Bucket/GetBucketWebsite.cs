
namespace Vessel3.Server.S3.Bucket;

internal sealed class GetBucketWebsite(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.Website);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        if (!registry.GetWebsite(bucket).TryGetValue(out var cfg, out var err))
            return http.Map(err);

        if (cfg is null) return http.Map(new NoSuchWebsiteConfigurationError(bucket));

        ctx.Response.ContentType = "application/xml";
        await xml.WriteWebsiteConfiguration(ctx.Response.Body, cfg, ctx.RequestAborted);
        return Results.Empty;
    }
}
