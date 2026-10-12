namespace Vessel3.Server.S3.Key;

internal sealed class PutObjectTagging(IObjectStore objects, IS3XmlReader reader, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Put, S3KeySubresource.Tagging);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var readResult = await reader.ReadTagging(ctx.Request.Body, ctx.RequestAborted);
        if (!readResult.TryGetValue(out var tagsValue, out var err))
            return http.Map(err);

        var putResult = objects.PutTagging(bucket, key, ctx.VersionId(), tagsValue);
        if (!putResult.TryGetValue(out var outcome, out var putErr))
            return http.Map(putErr);

        if (!string.IsNullOrEmpty(outcome.VersionId))
            ctx.Response.Headers["x-amz-version-id"] = outcome.VersionId;

        return Results.Ok();
    }
}
