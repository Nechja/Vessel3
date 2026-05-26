namespace Vessel3.Server.S3.Key;

internal sealed class GetObjectLegalHold(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Get, S3KeySubresource.LegalHold);

    public Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var versionId = ctx.VersionId() ?? registry.CurrentVersionOf(bucket, key);
        return versionId is null
            ? Task.FromResult(http.Map(new NoSuchKeyError(key)))
            : registry.GetLegalHold(bucket, key, versionId).Match<Task<IResult>>(
                async on =>
                {
                    ctx.Response.ContentType = "application/xml";
                    await xml.WriteLegalHold(ctx.Response.Body, on, ctx.RequestAborted);
                    return Results.Empty;
                },
                err => Task.FromResult(http.Map(err)));
    }
}
