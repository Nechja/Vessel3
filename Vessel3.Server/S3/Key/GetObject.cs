using System.Globalization;
using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3.Key;

internal sealed class GetObject(IObjectStore objects, IHttpResultMapper http, IPreconditionEvaluator pre) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Get, S3KeySubresource.None);

    public Task<IResult> Invoke(string bucket, string key, HttpContext ctx) =>
        Task.FromResult(objects.Get(bucket, key, Nullify(ctx.Request.Query["versionId"].ToString())).Match<IResult>(
            ok =>
            {
                var req = ctx.Request;
                var res = ctx.Response;
                var precond = pre.EvaluateForRead(req.Headers, ok.Etag, ok.LastModified);
                if (precond is Precondition.NotModified)
                {
                    ok.Body.Dispose();
                    return Results.StatusCode(304);
                }
                if (precond is Precondition.Failed)
                {
                    ok.Body.Dispose();
                    return Results.StatusCode(412);
                }
                res.Headers.ETag = $"\"{ok.Etag}\"";
                foreach (var (k, v) in ok.Metadata) res.Headers[$"x-amz-meta-{k}"] = v;
                EmitSystemHeaders(res.Headers, ok.SystemHeaders);

                var rangeRaw = req.Headers.Range.ToString();
                var isRangedSlice = false;
                if (!string.IsNullOrEmpty(rangeRaw))
                {
                    var parsed = ParseByteRange(rangeRaw, ok.Size);
                    switch (parsed)
                    {
                        case ByteRange.Unsatisfiable:
                            ok.Body.Dispose();
                            res.Headers["Content-Range"] = $"bytes */{ok.Size.ToString(CultureInfo.InvariantCulture)}";
                            return Results.StatusCode(416);
                        case ByteRange.Ignored:
                            req.Headers.Remove("Range");
                            break;
                        case ByteRange.Normal n:
                            req.Headers.Range = $"bytes={n.Start.ToString(CultureInfo.InvariantCulture)}-{n.End.ToString(CultureInfo.InvariantCulture)}";
                            isRangedSlice = true;
                            break;
                    }
                }

                if (!isRangedSlice)
                    ChecksumHeaders.Emit(res.Headers, ok.Checksums, fallbackSha256Hex: ok.Sha256);

                return Results.File(
                    ok.Body,
                    ok.ContentType,
                    lastModified: ok.LastModified,
                    enableRangeProcessing: true);
            },
            http.Map));
}
