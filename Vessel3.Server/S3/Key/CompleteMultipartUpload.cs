namespace Vessel3.Server.S3.Key;

internal sealed class CompleteMultipartUpload(IMultipartStore multipart, IS3XmlReader reader, IS3XmlWriter xml, IHttpResultMapper http) : IS3KeyAction
{
    public S3KeyRoute Route => new(HttpMethods.Post, S3KeySubresource.UploadId);

    public async Task<IResult> Invoke(string bucket, string key, HttpContext ctx)
    {
        var uploadId = ctx.Request.Query["uploadId"].ToString();
        if (!(await reader.ReadCompleteMultipartUploadRequest(ctx.Request.Body, ctx.RequestAborted)).TryGetValue(out var parsedParts, out var err))
            return http.Map(err);
        var clientParts = parsedParts.Select(p => (p.Number, p.Etag, p.Sums)).ToList();

        var compositeAlgo = ResolveCompositeAlgo(ctx.Request.Headers["x-amz-sdk-checksum-algorithm"].ToString(), parsedParts);

        var completed = await multipart.Complete(uploadId, clientParts, compositeAlgo, ctx.RequestAborted);
        return completed.Match<IResult>(
            outcome =>
            {
                ctx.Response.ContentType = "application/xml";
                var partsCount = clientParts.Count;
                return Results.Stream(async stream =>
                    await xml.WriteCompleteMultipartUploadResult(stream, bucket, key, outcome.Etag, outcome.Checksums, partsCount, ctx.RequestAborted),
                    "application/xml");
            },
            http.Map);
    }

    private static ChecksumAlgorithm? ResolveCompositeAlgo(string sdkAlgo, IReadOnlyList<CompletedPart> parts)
    {
        if (!string.IsNullOrEmpty(sdkAlgo) && ChecksumAlgorithms.TryParseName(sdkAlgo, out var a)) return a;
        foreach (var p in parts)
        {
            if (p.Sums is null) continue;
            if (p.Sums.Crc32 is not null) return ChecksumAlgorithm.Crc32;
            if (p.Sums.Crc32C is not null) return ChecksumAlgorithm.Crc32C;
            if (p.Sums.Sha1 is not null) return ChecksumAlgorithm.Sha1;
            if (p.Sums.Sha256 is not null) return ChecksumAlgorithm.Sha256;
        }
        return null;
    }
}
