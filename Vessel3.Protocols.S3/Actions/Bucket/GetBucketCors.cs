
namespace Vessel3.Server.S3.Bucket;

internal sealed class GetBucketCors(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.Cors);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        registry.GetCors(bucket).Match<Task<IResult>>(
            async cfg =>
            {
                if (cfg is null) return http.Map(new NoSuchCORSConfigurationError(bucket));
                ctx.Response.ContentType = "application/xml";
                await xml.WriteCorsConfiguration(ctx.Response.Body, cfg, ctx.RequestAborted);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
}
