using System.Globalization;
using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3.Key;

internal sealed class HeadObject(IObjectStore objects, IHttpResultMapper http, IPreconditionEvaluator pre) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Head, S3KeySubresource.None);

    public Task<IResult> Invoke(string bucket, string key, HttpContext ctx) =>
        Task.FromResult(objects.Stat(bucket, key, ctx.VersionId()).Match<IResult>(
            stat =>
            {
                var res = ctx.Response;
                var precond = pre.EvaluateForRead(ctx.Request.Headers, stat.Etag, stat.LastModified);
                if (precond is Precondition.NotModified) return Results.StatusCode(304);
                if (precond is Precondition.Failed) return Results.StatusCode(412);
                res.ContentLength = stat.Size;
                res.ContentType = stat.ContentType;
                res.Headers.ETag = $"\"{stat.Etag}\"";
                ChecksumHeaders.Emit(res.Headers, stat.Checksums, fallbackSha256Hex: stat.Sha256);
                res.Headers.LastModified = stat.LastModified.ToString("R", CultureInfo.InvariantCulture);
                foreach (var (k, v) in stat.Metadata) res.Headers[$"x-amz-meta-{k}"] = v;
                EmitSystemHeaders(res.Headers, stat.SystemHeaders);
                return Results.Empty;
            },
            http.Map));
}
