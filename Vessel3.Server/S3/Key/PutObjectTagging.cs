using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3.Key;

internal sealed class PutObjectTagging(IObjectStore objects, IS3XmlReader reader, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Put, S3KeySubresource.Tagging);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var parsedTags = await reader.ReadTagging(ctx.Request.Body, ctx.RequestAborted);
        if (parsedTags is Result<IReadOnlyDictionary<string, string>>.Failure tf) return http.Map(tf.Error);
        var tagsValue = ((Result<IReadOnlyDictionary<string, string>>.Success)parsedTags).Value;
        var versionIdQ = Nullify(ctx.Request.Query["versionId"].ToString());
        return objects.PutTagging(bucket, key, versionIdQ, tagsValue).Match<IResult>(
            outcome =>
            {
                if (!string.IsNullOrEmpty(outcome.VersionId))
                    ctx.Response.Headers["x-amz-version-id"] = outcome.VersionId;
                return Results.Ok();
            },
            http.Map);
    }
}
