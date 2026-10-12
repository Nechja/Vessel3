namespace Vessel3.Server.S3.Key;

internal sealed class GetObjectRetention(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Get, S3KeySubresource.Retention);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var versionId = ctx.VersionId() ?? registry.CurrentVersionOf(bucket, key);
        if (versionId is null) return http.Map(new NoSuchKeyError(key));

        if (!registry.GetRetention(bucket, key, versionId).TryGetValue(out var ret, out var err))
            return http.Map(err);

        if (ret is null) return http.Map(new NoSuchObjectLockConfigurationError($"{bucket}/{key}"));

        ctx.Response.ContentType = "application/xml";
        await xml.WriteRetention(ctx.Response.Body, ret, ctx.RequestAborted);
        return Results.Empty;
    }
}
