using System.IO.Hashing;
#pragma warning disable CA5350
using System.Security.Cryptography;

namespace Vessel3.Server;

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

    public static (string Crc32, string Crc32C, string Sha1, string Sha256) ComputeAll(ReadOnlySpan<byte> data)
    {
        var c32 = CrcUInt32ToHex(System.IO.Hashing.Crc32.HashToUInt32(data));
        var c32c = CrcUInt32ToHex(Vessel3.Server.Crc32C.HashToUInt32(data));
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

    public static ChecksumSet MergeTrailers(ChecksumSet existing, Stream body)
    {
        if (body is not Vessel3.Server.S3.AwsChunkedStream chunked) return existing;
        var trailers = chunked.Trailers;
        if (trailers.Count is 0) return existing;
        string? Read(string name, string? current) =>
            current is not null ? current
            : !trailers.TryGetValue(name, out var raw) || string.IsNullOrEmpty(raw) ? null
            : Base64ToHex(raw);
        return new ChecksumSet(
            Read(HeaderCrc32, existing.Crc32),
            Read(HeaderCrc32C, existing.Crc32C),
            Read(HeaderSha1, existing.Sha1),
            Read(HeaderSha256, existing.Sha256));
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
                var h = new Vessel3.Server.Crc32C();
                foreach (var hex in partHexValues) h.Append(Convert.FromHexString(hex));
                return CrcUInt32ToHex(h.GetCurrentHashAndReset());
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(algo));
        }
    }
}

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
