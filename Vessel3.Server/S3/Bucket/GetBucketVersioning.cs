using Vessel3.Server.Storage;

namespace Vessel3.Server.S3.Bucket;

internal sealed class GetBucketVersioning(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Get, S3BucketSubresource.Versioning);

    public Task<IResult> Invoke(string bucket, HttpContext ctx) =>
        registry.GetVersioning(bucket).Match<Task<IResult>>(
            async status =>
            {
                ctx.Response.ContentType = "application/xml";
                await xml.WriteVersioningConfiguration(ctx.Response.Body, status, ctx.RequestAborted);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
}
