namespace Vessel3.Server.S3;

internal static class ChecksumHeaders
{
    public const string Pending = "__PENDING__";
    private const string Malformed = "__MALFORMED__";

    public static ChecksumSet? ParseDeclared(IHeaderDictionary headers)
    {
        string? Decode(string name)
        {
            var raw = headers[name].ToString();
            return string.IsNullOrEmpty(raw)
                ? null
                : ChecksumAlgorithms.Base64ToHex(raw) ?? Malformed;
        }
        var c32 = Decode(ChecksumAlgorithms.HeaderCrc32);
        var c32c = Decode(ChecksumAlgorithms.HeaderCrc32C);
        var s1 = Decode(ChecksumAlgorithms.HeaderSha1);
        var s256 = Decode(ChecksumAlgorithms.HeaderSha256);
        if (c32 is Malformed || c32c is Malformed || s1 is Malformed || s256 is Malformed)
            return null;
        var trailers = headers["x-amz-trailer"].ToString();
        if (!string.IsNullOrEmpty(trailers))
        {
            if (c32 is null && trailers.Contains(ChecksumAlgorithms.HeaderCrc32, StringComparison.OrdinalIgnoreCase)) c32 = Pending;
            if (c32c is null && trailers.Contains(ChecksumAlgorithms.HeaderCrc32C, StringComparison.OrdinalIgnoreCase)) c32c = Pending;
            if (s1 is null && trailers.Contains(ChecksumAlgorithms.HeaderSha1, StringComparison.OrdinalIgnoreCase)) s1 = Pending;
            if (s256 is null && trailers.Contains(ChecksumAlgorithms.HeaderSha256, StringComparison.OrdinalIgnoreCase)) s256 = Pending;
        }
        return new ChecksumSet(c32, c32c, s1, s256);
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
