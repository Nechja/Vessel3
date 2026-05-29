using System.Globalization;
using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3.Key;

internal sealed class UploadPart(IMultipartStore multipart, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Put, S3KeySubresource.UploadId);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var partNumberRaw = req.Query["partNumber"].ToString();
        if (string.IsNullOrEmpty(partNumberRaw))
            return http.Map(new InvalidPathError("PUT with uploadId requires partNumber"));
        if (!int.TryParse(partNumberRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var partNumber))
            return http.Map(new InvalidPartError($"partNumber {partNumberRaw} not an integer"));

        var (partBody, partLength) = DecodeRequestBody(req);
        var partChecksums = ChecksumHeaders.ParseDeclared(req.Headers);
        if (partChecksums is null)
            return http.Map(new BadDigestError("malformed x-amz-checksum-* header (base64 expected)"));
        var partResult = await multipart.UploadPart(req.Query["uploadId"].ToString(), partNumber, partBody, partLength, partChecksums, ctx.RequestAborted);
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
