
namespace Vessel3.Server.S3.Bucket;

internal sealed class GetBucketAcl(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.Acl);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        if (!registry.GetAccess(bucket).TryGetValue(out var access, out var err))
            return http.Map(err);

        ctx.Response.ContentType = "application/xml";
        await xml.WriteAccessControlPolicy(ctx.Response.Body, "vessel3", access.PublicRead, ctx.RequestAborted);
        return Results.Empty;
    }
}
