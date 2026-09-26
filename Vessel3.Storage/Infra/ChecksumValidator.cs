using System.Diagnostics.CodeAnalysis;

namespace Vessel3.Storage;

internal static class ChecksumValidator
{
    public static bool Validate(
        StoredBlob blob,
        DeclaredChecksums declared,
        Stream body,
        out ChecksumSet toStore,
        [NotNullWhen(false)] out Error? error)
    {
        toStore = ChecksumSet.Empty;
        error = null;

        var trailers = (body as ITrailerStream)?.Trailers;

        if (!TryResolve(declared.Crc32, trailers, ChecksumAlgorithms.HeaderCrc32, out var c32Resolved, out error))
            return false;
        if (!TryResolve(declared.Crc32C, trailers, ChecksumAlgorithms.HeaderCrc32C, out var c32cResolved, out error))
            return false;
        if (!TryResolve(declared.Sha1, trailers, ChecksumAlgorithms.HeaderSha1, out var s1Resolved, out error))
            return false;
        if (!TryResolve(declared.Sha256, trailers, ChecksumAlgorithms.HeaderSha256, out var s256Resolved, out error))
            return false;

        if (c32Resolved is not null && !string.Equals(c32Resolved, blob.Crc32, StringComparison.OrdinalIgnoreCase))
        {
            error = new BadDigestError($"crc32 declared (hex){c32Resolved}, actual {blob.Crc32}");
            return false;
        }

        if (c32cResolved is not null && !string.Equals(c32cResolved, blob.Crc32C, StringComparison.OrdinalIgnoreCase))
        {
            error = new BadDigestError($"crc32c declared (hex){c32cResolved}, actual {blob.Crc32C}");
            return false;
        }

        if (s1Resolved is not null && !string.Equals(s1Resolved, blob.Sha1, StringComparison.OrdinalIgnoreCase))
        {
            error = new BadDigestError($"sha1 declared (hex){s1Resolved}, actual {blob.Sha1}");
            return false;
        }

        if (s256Resolved is not null && !string.Equals(s256Resolved, blob.Sha, StringComparison.OrdinalIgnoreCase))
        {
            error = new BadDigestError($"sha256(checksum) declared (hex){s256Resolved}, actual {blob.Sha}");
            return false;
        }

        toStore = new ChecksumSet(
            c32Resolved is null ? null : blob.Crc32,
            c32cResolved is null ? null : blob.Crc32C,
            s1Resolved is null ? null : blob.Sha1,
            s256Resolved is null ? null : blob.Sha);

        return true;
    }

    private static bool TryResolve(
        ChecksumTarget target,
        IReadOnlyDictionary<string, string>? trailers,
        string headerName,
        out string? resolvedHex,
        [NotNullWhen(false)] out Error? error)
    {
        resolvedHex = null;
        error = null;

        if (target.IsTrailing)
        {
            if (trailers is null || !trailers.TryGetValue(headerName, out var raw) || string.IsNullOrEmpty(raw))
            {
                error = new InvalidRequestError($"declared {headerName} trailer was not sent");
                return false;
            }

            resolvedHex = ChecksumAlgorithms.Base64ToHex(raw);
            if (resolvedHex is null)
            {
                error = new BadDigestError($"malformed {headerName} trailer");
                return false;
            }
            return true;
        }

        if (target.IsProvided)
        {
            resolvedHex = target.Value;
        }

        return true;
    }
}
