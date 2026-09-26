
namespace Vessel3.Server.S3.Bucket;

internal sealed class GetBucketAcl(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.Acl);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        registry.GetAccess(bucket).Match<Task<IResult>>(
            async access =>
            {
                ctx.Response.ContentType = "application/xml";
                await xml.WriteAccessControlPolicy(ctx.Response.Body, "vessel3", access.PublicRead, ctx.RequestAborted);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
}
