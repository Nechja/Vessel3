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
        {
            return Task.FromResult(http.Map(err));
        }

        if (EvaluatePreconditions(preconditions, ctx.Request, stat) is { } preconditionResult)
        {
            return Task.FromResult(preconditionResult);
        }

        EmitHeaders(ctx.Response, stat);
        return Task.FromResult<IResult>(Results.Empty);
    }

    private static IResult? EvaluatePreconditions(IPreconditionEvaluator evaluator, HttpRequest request, ObjectStat stat)
    {
        var readPreconditions = S3HeaderCodec.ExtractReadPreconditions(request.Headers);
        var evaluation = evaluator.Evaluate(readPreconditions, stat.Etag, stat.LastModified);
        return evaluation switch
        {
            Precondition.NotModified => Results.StatusCode(304),
            Precondition.Failed => Results.StatusCode(412),
            _ => null,
        };
    }

    private static void EmitHeaders(HttpResponse response, ObjectStat stat)
    {
        response.ContentLength = stat.Size;
        response.ContentType = stat.ContentType;
        response.Headers.ETag = $"\"{stat.Etag}\"";
        ChecksumHeaders.Emit(response.Headers, stat.Checksums, fallbackSha256Hex: stat.Sha256);
        response.Headers.LastModified = stat.LastModified.ToString("R", CultureInfo.InvariantCulture);
        foreach (var (key, value) in stat.Metadata)
        {
            response.Headers[$"{S3Headers.UserMetaPrefix}{key}"] = value;
        }

        S3HeaderCodec.EmitSystemHeaders(response.Headers, stat.SystemHeaders);
    }
}
