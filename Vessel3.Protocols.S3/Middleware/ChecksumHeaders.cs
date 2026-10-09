using Microsoft.Extensions.Primitives;

namespace Vessel3.Server.S3;

internal static class ChecksumHeaders
{
    public const string HeaderCrc32 = "x-amz-checksum-crc32";
    public const string HeaderCrc32C = "x-amz-checksum-crc32c";
    public const string HeaderSha1 = "x-amz-checksum-sha1";
    public const string HeaderSha256 = "x-amz-checksum-sha256";

    public static string HeaderFor(ChecksumAlgorithm a) => a switch
    {
        ChecksumAlgorithm.Crc32 => HeaderCrc32,
        ChecksumAlgorithm.Crc32C => HeaderCrc32C,
        ChecksumAlgorithm.Sha1 => HeaderSha1,
        ChecksumAlgorithm.Sha256 => HeaderSha256,
        _ => throw new ArgumentOutOfRangeException(nameof(a)),
    };

    public static DeclaredChecksums? ParseDeclared(IHeaderDictionary headers)
    {
        var hasTrailers = headers.TryGetValue("x-amz-trailer", out var trailersVal) && !StringValues.IsNullOrEmpty(trailersVal);
        var hasC32 = headers.TryGetValue(HeaderCrc32, out var c32Val) && !StringValues.IsNullOrEmpty(c32Val);
        var hasC32c = headers.TryGetValue(HeaderCrc32C, out var c32cVal) && !StringValues.IsNullOrEmpty(c32cVal);
        var hasSha1 = headers.TryGetValue(HeaderSha1, out var s1Val) && !StringValues.IsNullOrEmpty(s1Val);
        var hasSha256 = headers.TryGetValue(HeaderSha256, out var s256Val) && !StringValues.IsNullOrEmpty(s256Val);

        if (!hasTrailers && !hasC32 && !hasC32c && !hasSha1 && !hasSha256)
        {
            return DeclaredChecksums.Empty;
        }

        var trailers = hasTrailers ? trailersVal.ToString() : string.Empty;

        return !TryParseTarget(hasC32, c32Val, HeaderCrc32, trailers, hasTrailers, out var c32)
            || !TryParseTarget(hasC32c, c32cVal, HeaderCrc32C, trailers, hasTrailers, out var c32c)
            || !TryParseTarget(hasSha1, s1Val, HeaderSha1, trailers, hasTrailers, out var s1)
            || !TryParseTarget(hasSha256, s256Val, HeaderSha256, trailers, hasTrailers, out var s256)
            ? null
            : new DeclaredChecksums(c32, c32c, s1, s256);
    }

    private static bool TryParseTarget(
        bool hasHeader,
        StringValues headerVal,
        string headerName,
        string trailers,
        bool hasTrailers,
        out ChecksumTarget target)
    {
        if (hasHeader)
        {
            var hex = ChecksumAlgorithms.Base64ToHex(headerVal.ToString());
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
        if (sums.Crc32 is { } c32) headers[HeaderCrc32] = ChecksumAlgorithms.HexToBase64(c32);
        if (sums.Crc32C is { } c32c) headers[HeaderCrc32C] = ChecksumAlgorithms.HexToBase64(c32c);
        if (sums.Sha1 is { } s1) headers[HeaderSha1] = ChecksumAlgorithms.HexToBase64(s1);
        if (sums.Sha256 is { } s256)
            headers[HeaderSha256] = ChecksumAlgorithms.HexToBase64(s256);
        else if (!string.IsNullOrEmpty(fallbackSha256Hex))
            headers[HeaderSha256] = ChecksumAlgorithms.HexToBase64(fallbackSha256Hex);
    }
}
