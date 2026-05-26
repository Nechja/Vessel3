namespace Vessel3.Server.S3.Key;

internal sealed class PutObjectLegalHold(IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Put, S3KeySubresource.LegalHold);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        if (!(await reader.ReadLegalHold(ctx.Request.Body, ctx.RequestAborted)).TryGetValue(out var on, out var err))
            return http.Map(err);
        var resolvedVersion = ctx.VersionId() ?? registry.CurrentVersionOf(bucket, key);
        return resolvedVersion is null
            ? http.Map(new NoSuchKeyError(key))
            : registry.PutLegalHold(bucket, key, resolvedVersion, on).Match<IResult>(
                _ => Results.Ok(),
                http.Map);
    }
}
