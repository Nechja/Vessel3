namespace Vessel3.Server.S3.Key;

internal sealed class GetObjectTagging(IObjectStore objects, IS3XmlWriter xml, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Get, S3KeySubresource.Tagging);

    public Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        if (!objects.GetTagging(bucket, key, ctx.VersionId()).TryGetValue(out var tags, out var err))
            return Task.FromResult(http.Map(err));

        ctx.Response.ContentType = "application/xml";
        return Task.FromResult<IResult>(Results.Stream(async stream => await xml.WriteTagging(stream, tags, ctx.RequestAborted), "application/xml"));
    }
}
