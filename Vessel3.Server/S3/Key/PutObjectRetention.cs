using System.Globalization;
using Vessel3.Server.Storage;
using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3.Key;

internal sealed class PutObjectRetention(IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Put, S3KeySubresource.Retention);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var versionId = Nullify(ctx.Request.Query["versionId"].ToString());
        var bypass = ctx.Request.Headers["x-amz-bypass-governance-retention"].ToString()
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        var parsed = await reader.ReadRetention(ctx.Request.Body, ctx.RequestAborted);
        if (parsed is Result<Retention>.Failure pf) return http.Map(pf.Error);
        var retention = ((Result<Retention>.Success)parsed).Value;
        var resolvedVersion = versionId
            ?? (registry.GetCurrentPut(bucket, key) is Result<PutEntry?>.Success { Value: { } cur } ? cur.VersionId : null);
        return resolvedVersion is null
            ? http.Map(new NoSuchKeyError(key))
            : registry.PutRetention(bucket, key, resolvedVersion, retention, bypass).Match<IResult>(
                _ => Results.Ok(),
                http.Map);
    }
}
