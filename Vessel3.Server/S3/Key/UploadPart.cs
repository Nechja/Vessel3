using System.Globalization;
using Vessel3.Server.Storage;
using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3.Key;

internal sealed class UploadPart(IObjectStore objects, IMultipartStore multipart, IS3XmlWriter xml, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Put, S3KeySubresource.UploadId);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var uploadId = req.Query["uploadId"].ToString();
        var partNumberRaw = req.Query["partNumber"].ToString();
        if (string.IsNullOrEmpty(partNumberRaw))
            return http.Map(new InvalidPathError("PUT with uploadId requires partNumber"));
        if (!int.TryParse(partNumberRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var partNumber))
            return http.Map(new InvalidPartError($"partNumber {partNumberRaw} not an integer"));

        var copySource = req.Headers["x-amz-copy-source"].ToString();
        return string.IsNullOrEmpty(copySource)
            ? await PartFromBody(req, res, uploadId, partNumber, ctx.RequestAborted)
            : await PartFromCopy(req, res, uploadId, partNumber, copySource, ctx.RequestAborted);
    }

    private async Task<IResult> PartFromCopy(HttpRequest req, HttpResponse res, string uploadId, int partNumber, string copySource, CancellationToken ct)
    {
        if (!TryParseCopySource(copySource, out var srcBucket, out var srcKey))
            return http.Map(new InvalidPathError($"x-amz-copy-source: {copySource}"));

        var srcResult = objects.Get(srcBucket, srcKey);
        if (srcResult is Result<StoredObject>.Failure srcFail) return http.Map(srcFail.Error);
        var src = ((Result<StoredObject>.Success)srcResult).Value;

        var rangeHeader = req.Headers["x-amz-copy-source-range"].ToString();
        long copyOffset = 0;
        long copyLength = src.Size;
        if (!string.IsNullOrEmpty(rangeHeader)
            && TryParseByteRange(rangeHeader, src.Size, out var rangeStart, out var rangeEnd))
        {
            copyOffset = rangeStart;
            copyLength = rangeEnd - rangeStart + 1;
        }

        try
        {
            if (copyOffset > 0) src.Body.Seek(copyOffset, SeekOrigin.Begin);
        }
        catch (NotSupportedException)
        {
            src.Body.Dispose();
            return http.Map(new InvalidPathError("source stream not seekable for ranged copy"));
        }

        var copyBody = new BoundedStream(src.Body, copyLength);
        var copyResult = await multipart.UploadPart(uploadId, partNumber, copyBody, copyLength, ChecksumSet.Empty, ct);
        return copyResult.Match<IResult>(
            outcome =>
            {
                res.ContentType = "application/xml";
                return Results.Stream(async stream =>
                    await xml.WriteCopyPartResult(stream, outcome.Etag, DateTimeOffset.UtcNow, ct),
                    "application/xml");
            },
            http.Map);
    }

    private async Task<IResult> PartFromBody(HttpRequest req, HttpResponse res, string uploadId, int partNumber, CancellationToken ct)
    {
        var (partBody, partLength) = DecodeRequestBody(req);
        var partChecksums = ChecksumHeaders.ParseDeclared(req.Headers);
        if (partChecksums is null)
            return http.Map(new BadDigestError("malformed x-amz-checksum-* header (base64 expected)"));
        var partResult = await multipart.UploadPart(uploadId, partNumber, partBody, partLength, partChecksums, ct);
        return partResult.Match<IResult>(
            outcome =>
            {
                res.Headers.ETag = $"\"{outcome.Etag}\"";
                ChecksumHeaders.Emit(res.Headers, outcome.Checksums, fallbackSha256Hex: "");
                return Results.Ok();
            },
            http.Map);
    }
}
