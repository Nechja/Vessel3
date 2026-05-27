namespace Vessel3.Server.S3.Key;

internal sealed class PutObjectRetention(IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Put, S3KeySubresource.Retention);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        if (!(await reader.ReadRetention(ctx.Request.Body, ctx.RequestAborted)).TryGetValue(out var retention, out var err))
            return http.Map(err);
        var resolvedVersion = ctx.VersionId() ?? registry.CurrentVersionOf(bucket, key);
        return resolvedVersion is null
            ? http.Map(new NoSuchKeyError(key))
            : registry.PutRetention(bucket, key, resolvedVersion, retention, ctx.BypassGovernanceRetention()).Match<IResult>(
                () => Results.Ok(),
                http.Map);
    }
}
