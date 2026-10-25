using System.Buffers;

namespace Vessel3.Primitives;

public static class Sha256Hex
{
    private static readonly SearchValues<char> HexLowerChars = SearchValues.Create("0123456789abcdef");

    public static bool IsValid(ReadOnlySpan<char> value) =>
        value.Length == 64 && !value.ContainsAnyExcept(HexLowerChars);

    public static bool IsValidShard(ReadOnlySpan<char> value) =>
        value.Length == 2 && !value.ContainsAnyExcept(HexLowerChars);
}
