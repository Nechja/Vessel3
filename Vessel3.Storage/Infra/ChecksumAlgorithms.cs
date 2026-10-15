#pragma warning disable CA5350
using System.Collections.Frozen;
using System.Security.Cryptography;

namespace Vessel3.Storage;

internal static class ChecksumAlgorithms
{
    private static readonly FrozenDictionary<string, ChecksumAlgorithm> AlgorithmNames =
        new Dictionary<string, ChecksumAlgorithm>(StringComparer.OrdinalIgnoreCase)
        {
            ["CRC32"] = ChecksumAlgorithm.Crc32,
            ["CRC32C"] = ChecksumAlgorithm.Crc32C,
            ["SHA1"] = ChecksumAlgorithm.Sha1,
            ["SHA256"] = ChecksumAlgorithm.Sha256,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<ChecksumAlgorithm, Func<IEnumerable<string>, string>> CompositeCalculators =
        new Dictionary<ChecksumAlgorithm, Func<IEnumerable<string>, string>>
        {
            [ChecksumAlgorithm.Sha1] = parts => CompositeIncrementalHash(HashAlgorithmName.SHA1, parts),
            [ChecksumAlgorithm.Sha256] = parts => CompositeIncrementalHash(HashAlgorithmName.SHA256, parts),
            [ChecksumAlgorithm.Crc32] = CompositeCrc32,
            [ChecksumAlgorithm.Crc32C] = CompositeCrc32C,
        }.ToFrozenDictionary();

    public static bool TryParseName(string name, out ChecksumAlgorithm algorithm) =>
        AlgorithmNames.TryGetValue(name.Trim(), out algorithm);

    public static (string Crc32, string Crc32C, string Sha1, string Sha256) ComputeAll(ReadOnlySpan<byte> data)
    {
        var crc32 = CrcUInt32ToHex(System.IO.Hashing.Crc32.HashToUInt32(data));
        var crc32C = CrcUInt32ToHex(Crc32C.HashToUInt32(data));
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(data));
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(data));
        return (crc32, crc32C, sha1, sha256);
    }

    public static string CrcUInt32ToHex(uint value)
    {
        ReadOnlySpan<byte> destination = [
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF),
        ];
        return Convert.ToHexStringLower(destination);
    }

    public static string HexToBase64(string hex) =>
        string.IsNullOrEmpty(hex) ? "" : Convert.ToBase64String(Convert.FromHexString(hex));

    public static string? Base64ToHex(string base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        try
        {
            return Convert.ToHexStringLower(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string Composite(ChecksumAlgorithm algorithm, IEnumerable<string> partHexValues)
    {
        if (CompositeCalculators.TryGetValue(algorithm, out var calculator))
        {
            return calculator(partHexValues);
        }

        throw new ArgumentOutOfRangeException(nameof(algorithm));
    }

    private static string CompositeIncrementalHash(HashAlgorithmName algorithmName, IEnumerable<string> partHexValues)
    {
        using var incrementalHash = IncrementalHash.CreateHash(algorithmName);
        foreach (var hex in partHexValues)
        {
            incrementalHash.AppendData(Convert.FromHexString(hex));
        }
        return Convert.ToHexStringLower(incrementalHash.GetHashAndReset());
    }

    private static string CompositeCrc32(IEnumerable<string> partHexValues)
    {
        var hasher = new System.IO.Hashing.Crc32();
        foreach (var hex in partHexValues)
        {
            hasher.Append(Convert.FromHexString(hex));
        }
        return CrcUInt32ToHex(hasher.GetCurrentHashAsUInt32());
    }

    private static string CompositeCrc32C(IEnumerable<string> partHexValues)
    {
        var hasher = new Crc32C();
        foreach (var hex in partHexValues)
        {
            hasher.Append(Convert.FromHexString(hex));
        }
        return CrcUInt32ToHex(hasher.GetCurrentHashAndReset());
    }
}
