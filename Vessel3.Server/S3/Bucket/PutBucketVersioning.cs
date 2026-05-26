using Vessel3.Server.Storage;

namespace Vessel3.Server.S3.Bucket;

internal sealed class PutBucketVersioning(IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Put, S3BucketSubresource.Versioning);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        var parsed = await reader.ReadVersioningConfiguration(ctx.Request.Body, ctx.RequestAborted);
        if (parsed is Result<VersioningStatus>.Failure pf) return http.Map(pf.Error);
        var status = ((Result<VersioningStatus>.Success)parsed).Value;
        return registry.SetVersioning(bucket, status).Match<IResult>(_ => Results.Ok(), http.Map);
    }
}
