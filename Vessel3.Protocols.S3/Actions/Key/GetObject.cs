using System.Buffers;
using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace Vessel3.Server.S3.Key;

internal sealed class GetObject(
    IObjectStore objects,
    IHttpResultMapper http,
    IPreconditionEvaluator preconditions) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Get, S3KeySubresource.None);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var result = await objects.Get(bucket, key, ctx.VersionId(), ctx.RequestAborted);
        if (!result.TryGetValue(out var ok, out var err))
        {
            return http.Map(err);
        }

        if (EvaluatePreconditions(preconditions, ctx.Request, ok) is { } preconditionResult)
        {
            ok.Body.Dispose();
            return preconditionResult;
        }

        EmitMetadataHeaders(ctx.Response, ok);

        if (ApplyByteRange(ctx.Request, ctx.Response, ok, out var isRanged) is { } rangeError)
        {
            ok.Body.Dispose();
            return rangeError;
        }

        if (isRanged)
        {
            return Results.File(
                ok.Body,
                ok.ContentType,
                lastModified: ok.LastModified,
                enableRangeProcessing: true);
        }

        EmitPayloadHeaders(ctx.Response, ok);
        await using (ok.Body)
        {
            await StreamResponseBodyAsync(ok.Body, ctx.Response.Body, ok.Size, ctx.RequestAborted);
        }

        return Results.Empty;
    }

    private static IResult? EvaluatePreconditions(IPreconditionEvaluator evaluator, HttpRequest request, StoredObject ok)
    {
        var readPreconditions = S3HeaderCodec.ExtractReadPreconditions(request.Headers);
        var evaluation = evaluator.Evaluate(readPreconditions, ok.Etag, ok.LastModified);
        return evaluation switch
        {
            Precondition.NotModified => Results.StatusCode(304),
            Precondition.Failed => Results.StatusCode(412),
            _ => null,
        };
    }

    private static void EmitMetadataHeaders(HttpResponse response, StoredObject ok)
    {
        response.Headers.ETag = $"\"{ok.Etag}\"";
        foreach (var (key, value) in ok.Metadata)
        {
            response.Headers[$"{S3Headers.UserMetaPrefix}{key}"] = value;
        }
        S3HeaderCodec.EmitSystemHeaders(response.Headers, ok.SystemHeaders);
    }

    private static IResult? ApplyByteRange(HttpRequest request, HttpResponse response, StoredObject ok, out bool isRanged)
    {
        isRanged = false;
        if (!request.Headers.TryGetValue("Range", out var rangeHeader) || StringValues.IsNullOrEmpty(rangeHeader))
        {
            return null;
        }

        var parsedRange = S3ByteRange.Parse(rangeHeader.ToString(), ok.Size);
        switch (parsedRange)
        {
            case S3ByteRange.Unsatisfiable:
                response.Headers["Content-Range"] = $"bytes */{ok.Size.ToString(CultureInfo.InvariantCulture)}";
                return Results.StatusCode(416);
            case S3ByteRange.Ignored:
                request.Headers.Remove("Range");
                return null;
            case S3ByteRange.Normal normal:
                request.Headers.Range = $"bytes={normal.Start.ToString(CultureInfo.InvariantCulture)}-{normal.End.ToString(CultureInfo.InvariantCulture)}";
                isRanged = true;
                return null;
            default:
                return null;
        }
    }

    private static void EmitPayloadHeaders(HttpResponse response, StoredObject ok)
    {
        ChecksumHeaders.Emit(response.Headers, ok.Checksums, fallbackSha256Hex: ok.Sha256);
        response.StatusCode = 200;
        response.ContentLength = ok.Size;
        response.ContentType = ok.ContentType;
        response.Headers.LastModified = ok.LastModified.ToString("R", CultureInfo.InvariantCulture);
        response.Headers.AcceptRanges = "bytes";
    }

    private static async Task StreamResponseBodyAsync(Stream source, Stream destination, long size, CancellationToken ct)
    {
        if (size <= 0)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, 81920), ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
