using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3.Key;

internal sealed class CopyObject(IObjectStore objects, IS3XmlWriter xml, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Put, S3KeySubresource.None, S3KeyHeaderFlag.CopySource);

    public Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var ct = ctx.RequestAborted;

        var copySource = req.Headers["x-amz-copy-source"].ToString();
        var directive = req.Headers["x-amz-metadata-directive"].ToString();
        var metadataOverride = directive.Equals("REPLACE", StringComparison.OrdinalIgnoreCase)
            ? ExtractUserMetadata(req.Headers)
            : null;

        IReadOnlyDictionary<string, string>? tagsOverride = null;
        var tagDirective = req.Headers["x-amz-tagging-directive"].ToString();
        if (tagDirective.Equals("REPLACE", StringComparison.OrdinalIgnoreCase))
        {
            if (!TagSet.ParseHeader(req.Headers["x-amz-tagging"].ToString()).TryGetValue(out var parsed, out var hdrErr))
                return Task.FromResult(http.Map(hdrErr));
            tagsOverride = parsed;
        }

        return Task.FromResult(TryParseCopySource(copySource, out var srcBucket, out var srcKey)
            ? objects.Copy(bucket, key, srcBucket, srcKey, req.Headers, metadataOverride, tagsOverride).Match<IResult>(
                outcome =>
                {
                    res.Headers["x-amz-copy-source-version-id"] = outcome.VersionId;
                    res.ContentType = "application/xml";
                    return Results.Stream(async stream => await xml.WriteCopyObjectResult(stream, outcome, ct), "application/xml");
                },
                http.Map)
            : http.Map(new InvalidPathError($"x-amz-copy-source: {copySource}")));
    }
}
