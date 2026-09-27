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

        var trailing = (body as ITrailingChecksumProvider)?.TrailingChecksums;

        if (!TryResolve(declared.Crc32, trailing?.Crc32, "crc32", out var c32Resolved, out error))
            return false;
        if (!TryResolve(declared.Crc32C, trailing?.Crc32C, "crc32c", out var c32cResolved, out error))
            return false;
        if (!TryResolve(declared.Sha1, trailing?.Sha1, "sha1", out var s1Resolved, out error))
            return false;
        if (!TryResolve(declared.Sha256, trailing?.Sha256, "sha256", out var s256Resolved, out error))
            return false;

        if (IsChecksumMismatch(c32Resolved, blob.Crc32))
        {
            error = new BadDigestError($"crc32 declared (hex){c32Resolved}, actual {blob.Crc32}");
            return false;
        }

        if (IsChecksumMismatch(c32cResolved, blob.Crc32C))
        {
            error = new BadDigestError($"crc32c declared (hex){c32cResolved}, actual {blob.Crc32C}");
            return false;
        }

        if (IsChecksumMismatch(s1Resolved, blob.Sha1))
        {
            error = new BadDigestError($"sha1 declared (hex){s1Resolved}, actual {blob.Sha1}");
            return false;
        }

        if (IsChecksumMismatch(s256Resolved, blob.Sha))
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

    private static bool IsChecksumMismatch(string? resolved, string? actual) =>
        resolved is not null && !string.Equals(resolved, actual, StringComparison.OrdinalIgnoreCase);

    private static bool TryResolve(
        ChecksumTarget target,
        string? trailingValue,
        string algorithmName,
        out string? resolvedHex,
        [NotNullWhen(false)] out Error? error)
    {
        resolvedHex = null;
        error = null;

        if (target.IsTrailing)
        {
            if (string.IsNullOrEmpty(trailingValue))
            {
                error = new InvalidRequestError($"declared {algorithmName} trailer was not sent");
                return false;
            }

            resolvedHex = trailingValue;
            return true;
        }

        if (target.IsProvided)
        {
            resolvedHex = target.Value;
        }

        return true;
    }
}
