namespace Vessel3.Server.S3.Key;

internal sealed class GetObjectLegalHold(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Get, S3KeySubresource.LegalHold);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var versionId = ctx.VersionId() ?? registry.CurrentVersionOf(bucket, key);
        if (versionId is null) return http.Map(new NoSuchKeyError(key));

        if (!registry.GetLegalHold(bucket, key, versionId).TryGetValue(out var on, out var err))
            return http.Map(err);

        ctx.Response.ContentType = "application/xml";
        await xml.WriteLegalHold(ctx.Response.Body, on, ctx.RequestAborted);
        return Results.Empty;
    }
}
