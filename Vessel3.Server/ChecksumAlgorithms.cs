using System.IO.Hashing;
#pragma warning disable CA5350 // SHA1 is required by the S3 wire protocol; not used for security
using System.Security.Cryptography;

namespace Vessel3.Server;

/// <summary>Streaming CRC-32C (Castagnoli, polynomial 0x1EDC6F41, reflected). Hex-out.</summary>
internal sealed class Crc32C
{
    private static readonly uint[] Table = BuildTable();
    private uint state = 0xFFFFFFFFu;

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0x82F63B78u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        var c = state;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        state = c;
    }

    public uint GetCurrentHashAndReset()
    {
        var v = state ^ 0xFFFFFFFFu;
        state = 0xFFFFFFFFu;
        return v;
    }

    public static uint HashToUInt32(ReadOnlySpan<byte> data)
    {
        var h = new Crc32C();
        h.Append(data);
        return h.GetCurrentHashAndReset();
    }
}

/// <summary>
/// Wire-side checksum algorithms supported on PUT object / UploadPart / CompleteMultipartUpload.
/// S3 carries checksum values as base64 of the raw hash bytes; we convert to lowercase hex at the
/// storage boundary so all four sit alongside the existing SHA256/MD5 columns in the same shape.
/// Composite (multipart) value is hash(concat(decoded part-bytes)); the "-N" suffix is appended
/// by the caller. SHA1/SHA256 truly are composite; CRC32/CRC32C use the same shape — acceptable
/// for the COMPOSITE-mode S3 wire format we emit.
/// </summary>
internal enum ChecksumAlgorithm { Crc32, Crc32C, Sha1, Sha256 }

internal static class ChecksumAlgorithms
{
    public const string HeaderCrc32  = "x-amz-checksum-crc32";
    public const string HeaderCrc32C = "x-amz-checksum-crc32c";
    public const string HeaderSha1   = "x-amz-checksum-sha1";
    public const string HeaderSha256 = "x-amz-checksum-sha256";

    public static string HeaderFor(ChecksumAlgorithm a) => a switch
    {
        ChecksumAlgorithm.Crc32 => HeaderCrc32,
        ChecksumAlgorithm.Crc32C => HeaderCrc32C,
        ChecksumAlgorithm.Sha1 => HeaderSha1,
        ChecksumAlgorithm.Sha256 => HeaderSha256,
        _ => throw new ArgumentOutOfRangeException(nameof(a)),
    };

    public static bool TryParseName(string name, out ChecksumAlgorithm algo)
    {
        switch (name.Trim().ToUpperInvariant())
        {
            case "CRC32":   algo = ChecksumAlgorithm.Crc32;   return true;
            case "CRC32C":  algo = ChecksumAlgorithm.Crc32C;  return true;
            case "SHA1":    algo = ChecksumAlgorithm.Sha1;    return true;
            case "SHA256":  algo = ChecksumAlgorithm.Sha256;  return true;
            default:        algo = default;                   return false;
        }
    }

    /// <summary>Compute all four checksums of a buffer; returns lowercase hex (CRCs big-endian).</summary>
    public static (string Crc32, string Crc32C, string Sha1, string Sha256) ComputeAll(ReadOnlySpan<byte> data)
    {
        var c32 = CrcUInt32ToHex(System.IO.Hashing.Crc32.HashToUInt32(data));
        var c32c = CrcUInt32ToHex(Vessel3.Server.Crc32C.HashToUInt32(data));
        var s1 = Convert.ToHexStringLower(SHA1.HashData(data));
        var s256 = Convert.ToHexStringLower(SHA256.HashData(data));
        return (c32, c32c, s1, s256);
    }

    /// <summary>Big-endian 4 bytes of a CRC value, as lowercase hex — matches S3 wire byte order.</summary>
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

    /// <summary>Convert a hex-encoded hash to S3 wire base64.</summary>
    public static string HexToBase64(string hex) =>
        string.IsNullOrEmpty(hex) ? "" : Convert.ToBase64String(Convert.FromHexString(hex));

    /// <summary>Convert an S3 wire base64 checksum to lowercase hex; returns null on malformed input.</summary>
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

    /// <summary>Composite-mode object checksum: hash(concat(decoded-part-hex-bytes)) as lowercase hex.</summary>
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
                var h = new Vessel3.Server.Crc32C();
                foreach (var hex in partHexValues) h.Append(Convert.FromHexString(hex));
                return CrcUInt32ToHex(h.GetCurrentHashAndReset());
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(algo));
        }
    }
}

/// <summary>Per-PUT (and per-part) bundle of checksum hex values; null = not computed/declared.</summary>
internal sealed record ChecksumSet(string? Crc32, string? Crc32C, string? Sha1, string? Sha256)
{
    public static ChecksumSet Empty { get; } = new(null, null, null, null);

    public string? Get(ChecksumAlgorithm algo) => algo switch
    {
        ChecksumAlgorithm.Crc32  => Crc32,
        ChecksumAlgorithm.Crc32C => Crc32C,
        ChecksumAlgorithm.Sha1   => Sha1,
        ChecksumAlgorithm.Sha256 => Sha256,
        _ => null,
    };
}
