using System.Globalization;
using Vessel3.Server.Storage;
using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3.Key;

internal sealed class UploadPartCopy(IObjectStore objects, IMultipartStore multipart, IS3XmlWriter xml, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Put, S3KeySubresource.UploadId, S3KeyHeaderFlag.CopySource);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var ct = ctx.RequestAborted;
        var uploadId = req.Query["uploadId"].ToString();
        var partNumberRaw = req.Query["partNumber"].ToString();
        if (string.IsNullOrEmpty(partNumberRaw))
            return http.Map(new InvalidPathError("PUT with uploadId requires partNumber"));
        if (!int.TryParse(partNumberRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var partNumber))
            return http.Map(new InvalidPartError($"partNumber {partNumberRaw} not an integer"));

        var copySource = req.Headers["x-amz-copy-source"].ToString();
        if (!TryParseCopySource(copySource, out var srcBucket, out var srcKey))
            return http.Map(new InvalidPathError($"x-amz-copy-source: {copySource}"));

        if (!objects.Get(srcBucket, srcKey).TryGetValue(out var src, out var srcErr))
            return http.Map(srcErr);

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
}
