namespace Vessel3.Server.S3.Bucket;

internal sealed class PutObjectLockConfiguration(IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http) : IS3Action
{
    public S3Route Route => new(HttpMethods.Put, S3Subresource.ObjectLock);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        var parsed = await reader.ReadObjectLockConfiguration(ctx.Request.Body, ctx.RequestAborted);
        if (parsed is Result<ObjectLockConfig>.Failure pf) return http.Map(pf.Error);
        var cfg = ((Result<ObjectLockConfig>.Success)parsed).Value;
        return registry.SetObjectLock(bucket, cfg).Match<IResult>(_ => Results.Ok(), http.Map);
    }
}
