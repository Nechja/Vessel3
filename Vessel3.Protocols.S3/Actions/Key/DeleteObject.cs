namespace Vessel3.Server.S3.Key;

internal sealed class DeleteObject(IObjectStore objects, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Delete, S3KeySubresource.None);

    public Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var delVersionId = ctx.VersionId();
        var bypass = ctx.BypassGovernanceRetention();
        var result = delVersionId is null
            ? objects.Delete(bucket, key, bypass)
            : objects.DeleteVersion(bucket, key, delVersionId, bypass);

        return Task.FromResult(result.Match<IResult>(
            outcome =>
            {
                if (outcome.Found && !string.IsNullOrEmpty(outcome.VersionId))
                    ctx.Response.Headers["x-amz-version-id"] = outcome.VersionId;
                if (outcome.IsDeleteMarker)
                    ctx.Response.Headers["x-amz-delete-marker"] = "true";
                return Results.NoContent();
            },
            http.Map));
    }
}
