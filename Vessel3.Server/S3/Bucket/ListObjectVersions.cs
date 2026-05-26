using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3.Bucket;

internal sealed class ListObjectVersions(IBucketRegistry registry, IS3XmlWriter xml, IHttpResultMapper http) : IS3Action
{
    public S3Route Route => new(HttpMethods.Get, S3Subresource.Versions);

    public Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var prefix = Nullify(q["prefix"].ToString());
        var encodingType = Nullify(q["encoding-type"].ToString());
        var keyMarker = Nullify(q["key-marker"].ToString());
        var versionMax = Math.Clamp(int.TryParse(q["max-keys"].ToString(), out var mk) ? mk : 1000, 1, 1000);

        return registry.ListAllVersions(bucket, prefix, keyMarker, versionMax).Match<Task<IResult>>(
            async page =>
            {
                ctx.Response.ContentType = "application/xml";
                await xml.WriteListVersions(ctx.Response.Body, bucket, prefix, page.Entries, page.IsTruncated, versionMax, encodingType, ctx.RequestAborted);
                return Results.Empty;
            },
            err => Task.FromResult(http.Map(err)));
    }
}
