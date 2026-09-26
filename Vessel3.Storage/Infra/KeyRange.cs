using System.Buffers;
using System.Text;

namespace Vessel3.Storage;

internal static class KeyRange
{
    public static string? Successor(string prefix)
    {
        var remaining = prefix.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeLastFromUtf16(remaining, out var rune, out var charsConsumed);
            if (status != OperationStatus.Done) return null;

            remaining = remaining[..^charsConsumed];
            var next = rune.Value + 1;
            if (next == 0xD800) next = 0xE000;
            if (next > 0x10FFFF) continue;

            var nextRune = new Rune(next);
            var prefixLen = remaining.Length;
            var totalLen = prefixLen + nextRune.Utf16SequenceLength;

            return string.Create(totalLen, (prefix, prefixLen, nextRune), static (span, state) =>
            {
                state.prefix.AsSpan(0, state.prefixLen).CopyTo(span);
                state.nextRune.EncodeToUtf16(span[state.prefixLen..]);
            });
        }

        return null;
    }
}
