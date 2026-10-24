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
            Precondition.NotModified => Results.StatusCode(StatusCodes.Status304NotModified),
            Precondition.Failed => Results.StatusCode(StatusCodes.Status412PreconditionFailed),
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
        return parsedRange switch
        {
            S3ByteRange.Unsatisfiable => HandleUnsatisfiable(response, ok.Size),
            S3ByteRange.Ignored => HandleIgnored(request),
            S3ByteRange.Normal normal => HandleNormal(request, normal),
            _ => null,
        };
    }

    private static IResult HandleUnsatisfiable(HttpResponse response, long size)
    {
        response.Headers["Content-Range"] = $"bytes */{size.ToString(CultureInfo.InvariantCulture)}";
        return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
    }

    private static IResult? HandleIgnored(HttpRequest request)
    {
        request.Headers.Remove("Range");
        return null;
    }

    private static IResult? HandleNormal(HttpRequest request, S3ByteRange.Normal normal)
    {
        request.Headers.Range = $"bytes={normal.Start.ToString(CultureInfo.InvariantCulture)}-{normal.End.ToString(CultureInfo.InvariantCulture)}";
        return null;
    }
}
