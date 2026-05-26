namespace Vessel3.Server.S3.Key;

internal sealed class DeleteObjectTagging(IObjectStore objects, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Delete, S3KeySubresource.Tagging);

    public Task<IResult> Invoke(string bucket, string key, HttpContext ctx) =>
        Task.FromResult(objects.DeleteTagging(bucket, key, ctx.VersionId()).Match<IResult>(
            outcome =>
            {
                if (!string.IsNullOrEmpty(outcome.VersionId))
                    ctx.Response.Headers["x-amz-version-id"] = outcome.VersionId;
                return Results.NoContent();
            },
            http.Map));
}
