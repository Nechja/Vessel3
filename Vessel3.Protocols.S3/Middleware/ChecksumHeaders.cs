using Vessel3.Storage;

namespace Vessel3.Server.S3;

internal static class ChecksumHeaders
{
    public static DeclaredChecksums? ParseDeclared(IHeaderDictionary headers)
    {
        var trailers = headers["x-amz-trailer"].ToString();
        var hasTrailers = !string.IsNullOrEmpty(trailers);

        return !TryParseTarget(headers, ChecksumAlgorithms.HeaderCrc32, trailers, hasTrailers, out var c32)
            || !TryParseTarget(headers, ChecksumAlgorithms.HeaderCrc32C, trailers, hasTrailers, out var c32c)
            || !TryParseTarget(headers, ChecksumAlgorithms.HeaderSha1, trailers, hasTrailers, out var s1)
            || !TryParseTarget(headers, ChecksumAlgorithms.HeaderSha256, trailers, hasTrailers, out var s256)
            ? null
            : new DeclaredChecksums(c32, c32c, s1, s256);
    }

    private static bool TryParseTarget(
        IHeaderDictionary headers,
        string headerName,
        string trailers,
        bool hasTrailers,
        out ChecksumTarget target)
    {
        var raw = headers[headerName].ToString();
        if (!string.IsNullOrEmpty(raw))
        {
            var hex = ChecksumAlgorithms.Base64ToHex(raw);
            if (hex is null)
            {
                target = ChecksumTarget.None;
                return false;
            }
            target = ChecksumTarget.Provided(hex);
            return true;
        }

        target = hasTrailers && trailers.Contains(headerName, StringComparison.OrdinalIgnoreCase)
            ? ChecksumTarget.Trailing
            : ChecksumTarget.None;
        return true;
    }

    public static void Emit(IHeaderDictionary headers, ChecksumSet sums, string fallbackSha256Hex)
    {
        if (sums.Crc32 is { } c32) headers[ChecksumAlgorithms.HeaderCrc32] = ChecksumAlgorithms.HexToBase64(c32);
        if (sums.Crc32C is { } c32c) headers[ChecksumAlgorithms.HeaderCrc32C] = ChecksumAlgorithms.HexToBase64(c32c);
        if (sums.Sha1 is { } s1) headers[ChecksumAlgorithms.HeaderSha1] = ChecksumAlgorithms.HexToBase64(s1);
        if (sums.Sha256 is { } s256)
            headers[ChecksumAlgorithms.HeaderSha256] = ChecksumAlgorithms.HexToBase64(s256);
        else if (!string.IsNullOrEmpty(fallbackSha256Hex))
            headers[ChecksumAlgorithms.HeaderSha256] = ChecksumAlgorithms.HexToBase64(fallbackSha256Hex);
    }
}
