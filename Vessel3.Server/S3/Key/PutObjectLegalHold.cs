using Vessel3.Server.Storage;
using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3.Key;

internal sealed class PutObjectLegalHold(IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Put, S3KeySubresource.LegalHold);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var versionId = Nullify(ctx.Request.Query["versionId"].ToString());
        var parsed = await reader.ReadLegalHold(ctx.Request.Body, ctx.RequestAborted);
        if (parsed is Result<bool>.Failure pf) return http.Map(pf.Error);
        var on = ((Result<bool>.Success)parsed).Value;
        var resolvedVersion = versionId
            ?? (registry.GetCurrentPut(bucket, key) is Result<PutEntry?>.Success { Value: { } cur } ? cur.VersionId : null);
        return resolvedVersion is null
            ? http.Map(new NoSuchKeyError(key))
            : registry.PutLegalHold(bucket, key, resolvedVersion, on).Match<IResult>(
                _ => Results.Ok(),
                http.Map);
    }
}
