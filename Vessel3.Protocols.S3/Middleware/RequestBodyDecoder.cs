namespace Vessel3.Server.S3;

internal static class RequestBodyDecoder
{
    public static (Stream Body, long? DeclaredLength) Decode(HttpRequest req)
    {
        var contentSha = req.Headers["x-amz-content-sha256"].ToString();
        var isChunked = req.Headers.ContentEncoding.ToString().Contains("aws-chunked", StringComparison.Ordinal)
            || contentSha.Contains("STREAMING-", StringComparison.Ordinal);

        if (!isChunked)
        {
            return (req.Body, req.ContentLength);
        }

        var payloadHasPerChunkSignatures = contentSha.StartsWith("STREAMING-AWS4-HMAC-SHA256-PAYLOAD", StringComparison.Ordinal);
        var sigCtx = payloadHasPerChunkSignatures ? req.HttpContext.Items["sigctx"] as SignatureContext : null;
        var declared = long.TryParse(req.Headers["x-amz-decoded-content-length"].ToString(), out var dl) ? dl : (long?)null;
        return (new AwsChunkedStream(req.Body, sigCtx, declared), declared);
    }
}
