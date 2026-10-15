namespace Vessel3.Server.S3.Key;

internal sealed class CompleteMultipartUpload(IMultipartStore multipart, IS3XmlReader reader, IS3XmlWriter xml, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Post, S3KeySubresource.UploadId);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var uploadId = ctx.Request.Query.TryGetValue("uploadId", out var uploadIdValues) && uploadIdValues.Count > 0
            ? uploadIdValues[0] ?? string.Empty
            : string.Empty;

        var readResult = await reader.ReadCompleteMultipartUploadRequest(ctx.Request.Body, ctx.RequestAborted);
        if (!readResult.TryGetValue(out var parsedParts, out var readError))
        {
            return http.Map(readError);
        }

        var sdkAlgorithmName = ctx.Request.Headers.TryGetValue("x-amz-sdk-checksum-algorithm", out var algorithmValues) && algorithmValues.Count > 0
            ? algorithmValues[0] ?? string.Empty
            : string.Empty;

        var compositeAlgorithm = ResolveCompositeAlgo(sdkAlgorithmName, parsedParts);

        var completionResult = await multipart.Complete(uploadId, parsedParts, compositeAlgorithm, ctx.RequestAborted);
        if (!completionResult.TryGetValue(out var outcome, out var completionError))
        {
            return http.Map(completionError);
        }

        ctx.Response.ContentType = "application/xml";
        var partsCount = parsedParts.Count;
        return Results.Stream(async stream =>
            await xml.WriteCompleteMultipartUploadResult(stream, bucket, key, outcome.Etag, outcome.Checksums, partsCount, ctx.RequestAborted),
            "application/xml");
    }

    private static ChecksumAlgorithm? ResolveCompositeAlgo(string sdkAlgorithmName, IReadOnlyList<CompletedPart> parts)
    {
        if (!string.IsNullOrEmpty(sdkAlgorithmName) && ChecksumAlgorithms.TryParseName(sdkAlgorithmName, out var algorithm))
        {
            return algorithm;
        }

        foreach (var part in parts)
        {
            var match = part.Sums switch
            {
                { Crc32: not null } => ChecksumAlgorithm.Crc32,
                { Crc32C: not null } => ChecksumAlgorithm.Crc32C,
                { Sha1: not null } => ChecksumAlgorithm.Sha1,
                { Sha256: not null } => ChecksumAlgorithm.Sha256,
                _ => (ChecksumAlgorithm?)null,
            };

            if (match is not null) return match;
        }

        return null;
    }
}
