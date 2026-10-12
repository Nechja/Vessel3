
namespace Vessel3.Server.S3.Bucket;

internal sealed class GetBucketVersioning(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.Versioning);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        if (!registry.GetVersioning(bucket).TryGetValue(out var status, out var err))
            return http.Map(err);

        ctx.Response.ContentType = "application/xml";
        await xml.WriteVersioningConfiguration(ctx.Response.Body, status, ctx.RequestAborted);
        return Results.Empty;
    }
}
