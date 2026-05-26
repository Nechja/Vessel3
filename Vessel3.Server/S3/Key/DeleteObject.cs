using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3.Key;

internal sealed class DeleteObject(IObjectStore objects, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Delete, S3KeySubresource.None);

    public Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var delVersionId = Nullify(req.Query["versionId"].ToString());
        var bypassGovernance = req.Headers["x-amz-bypass-governance-retention"].ToString()
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        var result = delVersionId is null
            ? objects.Delete(bucket, key, bypassGovernance)
            : objects.DeleteVersion(bucket, key, delVersionId, bypassGovernance);

        return Task.FromResult(result.Match<IResult>(
            outcome =>
            {
                if (outcome.Found && !string.IsNullOrEmpty(outcome.VersionId))
                    res.Headers["x-amz-version-id"] = outcome.VersionId;
                if (outcome.IsDeleteMarker)
                    res.Headers["x-amz-delete-marker"] = "true";
                return Results.NoContent();
            },
            http.Map));
    }
}
