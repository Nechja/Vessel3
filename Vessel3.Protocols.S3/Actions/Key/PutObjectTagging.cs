namespace Vessel3.Server.S3.Key;

internal sealed class PutObjectTagging(IObjectStore objects, IS3XmlReader reader, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Put, S3KeySubresource.Tagging);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        return !(await reader.ReadTagging(ctx.Request.Body, ctx.RequestAborted)).TryGetValue(out var tagsValue, out var err)
            ? http.Map(err)
            : objects.PutTagging(bucket, key, ctx.VersionId(), tagsValue).Match<IResult>(
                outcome =>
                {
                    if (!string.IsNullOrEmpty(outcome.VersionId))
                        ctx.Response.Headers["x-amz-version-id"] = outcome.VersionId;
                    return Results.Ok();
                },
                http.Map);
    }
}
