using System.Buffers;
using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace Vessel3.Server.S3.Key;

internal sealed class GetObject(IObjectStore objects, IHttpResultMapper http, IPreconditionEvaluator pre) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Get, S3KeySubresource.None);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var result = await objects.Get(bucket, key, ctx.VersionId(), ctx.RequestAborted);
        if (!result.TryGetValue(out var ok, out var err))
            return http.Map(err);

        var req = ctx.Request;
        var res = ctx.Response;
        var readPre = S3HeaderCodec.ExtractReadPreconditions(req.Headers);
        var precond = pre.Evaluate(readPre, ok.Etag, ok.LastModified);
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
        S3HeaderCodec.EmitSystemHeaders(res.Headers, ok.SystemHeaders);

        var isRangedSlice = false;
        if (req.Headers.TryGetValue("Range", out var rangeVal) && !StringValues.IsNullOrEmpty(rangeVal))
        {
            var parsed = S3ByteRange.Parse(rangeVal.ToString(), ok.Size);
            switch (parsed)
            {
                case S3ByteRange.Unsatisfiable:
                    ok.Body.Dispose();
                    res.Headers["Content-Range"] = $"bytes */{ok.Size.ToString(CultureInfo.InvariantCulture)}";
                    return Results.StatusCode(416);
                case S3ByteRange.Ignored:
                    req.Headers.Remove("Range");
                    break;
                case S3ByteRange.Normal n:
                    req.Headers.Range = $"bytes={n.Start.ToString(CultureInfo.InvariantCulture)}-{n.End.ToString(CultureInfo.InvariantCulture)}";
                    isRangedSlice = true;
                    break;
            }
        }

        if (isRangedSlice)
        {
            return Results.File(
                ok.Body,
                ok.ContentType,
                lastModified: ok.LastModified,
                enableRangeProcessing: true);
        }

        ChecksumHeaders.Emit(res.Headers, ok.Checksums, fallbackSha256Hex: ok.Sha256);
        res.StatusCode = 200;
        res.ContentLength = ok.Size;
        res.ContentType = ok.ContentType;
        res.Headers.LastModified = ok.LastModified.ToString("R", CultureInfo.InvariantCulture);
        res.Headers.AcceptRanges = "bytes";

        await using (ok.Body)
        {
            if (ok.Size > 0)
            {
                var buf = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int n;
                    while ((n = await ok.Body.ReadAsync(buf.AsMemory(0, 81920), ctx.RequestAborted)) > 0)
                    {
                        await res.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
        }

        return Results.Empty;
    }
}
