using Vessel3.Server.Storage;

namespace Vessel3.Server.S3.Bucket;

internal sealed class PutBucketVersioning(IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Put, S3BucketSubresource.Versioning);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        return !(await reader.ReadVersioningConfiguration(ctx.Request.Body, ctx.RequestAborted)).TryGetValue(out var status, out var err)
            ? http.Map(err)
            : registry.SetVersioning(bucket, status).Match<IResult>(_ => Results.Ok(), http.Map);
    }
}
