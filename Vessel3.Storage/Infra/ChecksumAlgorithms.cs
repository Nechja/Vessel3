#pragma warning disable CA5350
using System.Security.Cryptography;

namespace Vessel3.Storage;

internal static class ChecksumAlgorithms
{

    public static bool TryParseName(string name, out ChecksumAlgorithm algo)
    {
        bool ok;
        (ok, algo) = name.Trim().ToUpperInvariant() switch
        {
            "CRC32"  => (true, ChecksumAlgorithm.Crc32),
            "CRC32C" => (true, ChecksumAlgorithm.Crc32C),
            "SHA1"   => (true, ChecksumAlgorithm.Sha1),
            "SHA256" => (true, ChecksumAlgorithm.Sha256),
            _        => (false, default),
        };
        return ok;
    }

    public static (string Crc32, string Crc32C, string Sha1, string Sha256) ComputeAll(ReadOnlySpan<byte> data)
    {
        var c32 = CrcUInt32ToHex(System.IO.Hashing.Crc32.HashToUInt32(data));
        var c32c = CrcUInt32ToHex(Crc32C.HashToUInt32(data));
        var s1 = Convert.ToHexStringLower(SHA1.HashData(data));
        var s256 = Convert.ToHexStringLower(SHA256.HashData(data));
        return (c32, c32c, s1, s256);
    }

    public static string CrcUInt32ToHex(uint v)
    {
        ReadOnlySpan<byte> dst = [
            (byte)((v >> 24) & 0xFF),
            (byte)((v >> 16) & 0xFF),
            (byte)((v >> 8) & 0xFF),
            (byte)(v & 0xFF),
        ];
        return Convert.ToHexStringLower(dst);
    }

    public static string HexToBase64(string hex) =>
        string.IsNullOrEmpty(hex) ? "" : Convert.ToBase64String(Convert.FromHexString(hex));

    public static string? Base64ToHex(string b64)
    {
        if (string.IsNullOrEmpty(b64)) return null;
        try
        {
            return Convert.ToHexStringLower(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string Composite(ChecksumAlgorithm algo, IEnumerable<string> partHexValues)
    {
        switch (algo)
        {
            case ChecksumAlgorithm.Sha1:
            {
                using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                foreach (var hex in partHexValues) h.AppendData(Convert.FromHexString(hex));
                return Convert.ToHexStringLower(h.GetHashAndReset());
            }
            case ChecksumAlgorithm.Sha256:
            {
                using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var hex in partHexValues) h.AppendData(Convert.FromHexString(hex));
                return Convert.ToHexStringLower(h.GetHashAndReset());
            }
            case ChecksumAlgorithm.Crc32:
            {
                var h = new System.IO.Hashing.Crc32();
                foreach (var hex in partHexValues) h.Append(Convert.FromHexString(hex));
                return CrcUInt32ToHex(h.GetCurrentHashAsUInt32());
            }
            case ChecksumAlgorithm.Crc32C:
            {
                var h = new Crc32C();
                foreach (var hex in partHexValues) h.Append(Convert.FromHexString(hex));
                return CrcUInt32ToHex(h.GetCurrentHashAndReset());
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(algo));
        }
    }
}
