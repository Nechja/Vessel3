namespace Vessel3.Server.S3.Bucket;

internal sealed class PutObjectLockConfiguration(IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Put, S3BucketSubresource.ObjectLock);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        var parsed = await reader.ReadObjectLockConfiguration(ctx.Request.Body, ctx.RequestAborted);
        if (parsed is Result<ObjectLockConfig>.Failure pf) return http.Map(pf.Error);
        var cfg = ((Result<ObjectLockConfig>.Success)parsed).Value;
        return registry.SetObjectLock(bucket, cfg).Match<IResult>(_ => Results.Ok(), http.Map);
    }
}
