using System.Globalization;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

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

        if (ApplyByteRange(ctx.Request, ctx.Response, ok) is { } rangeError)
        {
            ok.Body.Dispose();
            return rangeError;
        }

        ChecksumHeaders.Emit(ctx.Response.Headers, ok.Checksums, fallbackSha256Hex: ok.Sha256);

        return Results.File(
            ok.Body,
            ok.ContentType,
            lastModified: ok.LastModified,
            entityTag: new EntityTagHeaderValue($"\"{ok.Etag}\""),
            enableRangeProcessing: true);
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

    private static IResult? ApplyByteRange(HttpRequest request, HttpResponse response, StoredObject ok)
    {
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
                return null;
            default:
                return null;
        }
    }
}
