using System.Buffers;

namespace Vessel3.Primitives;

public static class Sha256Hex
{
    public const int HexLength = 64;
    public const int ShardLength = 2;
    private const string LowerHexAlphabet = "0123456789abcdef";

    private static readonly SearchValues<char> LowerHexChars = SearchValues.Create(LowerHexAlphabet);

    public static bool IsValid(ReadOnlySpan<char> value) =>
        value.Length == HexLength && !value.ContainsAnyExcept(LowerHexChars);

    public static bool IsValidShard(ReadOnlySpan<char> value) =>
        value.Length == ShardLength && !value.ContainsAnyExcept(LowerHexChars);
}
