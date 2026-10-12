using Microsoft.Extensions.Primitives;

namespace Vessel3.Server.S3;

internal static class ChecksumHeaders
{
    public const string HeaderCrc32 = "x-amz-checksum-crc32";
    public const string HeaderCrc32C = "x-amz-checksum-crc32c";
    public const string HeaderSha1 = "x-amz-checksum-sha1";
    public const string HeaderSha256 = "x-amz-checksum-sha256";

    public static string HeaderFor(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Crc32 => HeaderCrc32,
        ChecksumAlgorithm.Crc32C => HeaderCrc32C,
        ChecksumAlgorithm.Sha1 => HeaderSha1,
        ChecksumAlgorithm.Sha256 => HeaderSha256,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    public static DeclaredChecksums? ParseDeclared(IHeaderDictionary headers)
    {
        var hasTrailers = headers.TryGetValue(S3Headers.Trailer, out var trailersVal) && !StringValues.IsNullOrEmpty(trailersVal);
        var hasCrc32 = headers.TryGetValue(HeaderCrc32, out var crc32HeaderValue) && !StringValues.IsNullOrEmpty(crc32HeaderValue);
        var hasCrc32C = headers.TryGetValue(HeaderCrc32C, out var crc32cHeaderValue) && !StringValues.IsNullOrEmpty(crc32cHeaderValue);
        var hasSha1 = headers.TryGetValue(HeaderSha1, out var sha1HeaderValue) && !StringValues.IsNullOrEmpty(sha1HeaderValue);
        var hasSha256 = headers.TryGetValue(HeaderSha256, out var sha256HeaderValue) && !StringValues.IsNullOrEmpty(sha256HeaderValue);

        if (!hasTrailers && !hasCrc32 && !hasCrc32C && !hasSha1 && !hasSha256)
        {
            return DeclaredChecksums.Empty;
        }

        var trailers = hasTrailers ? trailersVal.ToString() : string.Empty;

        return !TryParseTarget(hasCrc32, crc32HeaderValue, HeaderCrc32, trailers, hasTrailers, out var crc32Target)
            || !TryParseTarget(hasCrc32C, crc32cHeaderValue, HeaderCrc32C, trailers, hasTrailers, out var crc32cTarget)
            || !TryParseTarget(hasSha1, sha1HeaderValue, HeaderSha1, trailers, hasTrailers, out var sha1Target)
            || !TryParseTarget(hasSha256, sha256HeaderValue, HeaderSha256, trailers, hasTrailers, out var sha256Target)
            ? null
            : new DeclaredChecksums(crc32Target, crc32cTarget, sha1Target, sha256Target);
    }

    private static bool TryParseTarget(
        bool hasHeader,
        StringValues headerValue,
        string headerName,
        string trailers,
        bool hasTrailers,
        out ChecksumTarget target)
    {
        if (hasHeader)
        {
            var hex = ChecksumAlgorithms.Base64ToHex(headerValue.ToString());
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
        if (sums.Crc32 is { } crc32Hex) headers[HeaderCrc32] = ChecksumAlgorithms.HexToBase64(crc32Hex);
        if (sums.Crc32C is { } crc32cHex) headers[HeaderCrc32C] = ChecksumAlgorithms.HexToBase64(crc32cHex);
        if (sums.Sha1 is { } sha1Hex) headers[HeaderSha1] = ChecksumAlgorithms.HexToBase64(sha1Hex);
        if (sums.Sha256 is { } sha256Hex)
            headers[HeaderSha256] = ChecksumAlgorithms.HexToBase64(sha256Hex);
        else if (!string.IsNullOrEmpty(fallbackSha256Hex))
            headers[HeaderSha256] = ChecksumAlgorithms.HexToBase64(fallbackSha256Hex);
    }
}
