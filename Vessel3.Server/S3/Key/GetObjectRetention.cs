namespace Vessel3.Server.S3.Key;

internal sealed class GetObjectRetention(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Get, S3KeySubresource.Retention);

    public Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var versionId = ctx.VersionId() ?? registry.CurrentVersionOf(bucket, key);
        return versionId is null
            ? Task.FromResult(http.Map(new NoSuchKeyError(key)))
            : registry.GetRetention(bucket, key, versionId).Match<Task<IResult>>(
                async ret =>
                {
                    if (ret is null) return http.Map(new NoSuchObjectLockConfigurationError($"{bucket}/{key}"));
                    ctx.Response.ContentType = "application/xml";
                    await xml.WriteRetention(ctx.Response.Body, ret, ctx.RequestAborted);
                    return Results.Empty;
                },
                err => Task.FromResult(http.Map(err)));
    }
}
