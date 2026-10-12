using System.Globalization;

namespace Vessel3.Server.S3.Key;

internal sealed class HeadObject(
    IObjectStore objects,
    IHttpResultMapper http,
    IPreconditionEvaluator preconditions) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Head, S3KeySubresource.None);

    public Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        if (!objects.Stat(bucket, key, ctx.VersionId()).TryGetValue(out var stat, out var err))
            return Task.FromResult(http.Map(err));

        var res = ctx.Response;
        var readPre = S3HeaderCodec.ExtractReadPreconditions(ctx.Request.Headers);
        var precond = preconditions.Evaluate(readPre, stat.Etag, stat.LastModified);
        if (precond is Precondition.NotModified)
        {
            return Task.FromResult<IResult>(Results.StatusCode(304));
        }

        if (precond is Precondition.Failed)
        {
            return Task.FromResult<IResult>(Results.StatusCode(412));
        }

        res.ContentLength = stat.Size;
        res.ContentType = stat.ContentType;
        res.Headers.ETag = $"\"{stat.Etag}\"";
        ChecksumHeaders.Emit(res.Headers, stat.Checksums, fallbackSha256Hex: stat.Sha256);
        res.Headers.LastModified = stat.LastModified.ToString("R", CultureInfo.InvariantCulture);
        foreach (var (metaKey, metaValue) in stat.Metadata)
        {
            res.Headers[$"{S3Headers.UserMetaPrefix}{metaKey}"] = metaValue;
        }

        S3HeaderCodec.EmitSystemHeaders(res.Headers, stat.SystemHeaders);
        return Task.FromResult<IResult>(Results.Empty);
    }
}
