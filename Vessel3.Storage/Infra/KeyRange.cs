using System.Text;

namespace Vessel3.Storage;

internal static class KeyRange
{
    public static string? Successor(string prefix)
    {
        var runes = prefix.EnumerateRunes().ToList();
        for (var i = runes.Count - 1; i >= 0; i--)
        {
            var next = runes[i].Value + 1;
            if (next == 0xD800) next = 0xE000;
            if (next > 0x10FFFF) continue;

            var sb = new StringBuilder(prefix.Length);
            for (var j = 0; j < i; j++) sb.Append(runes[j].ToString());
            sb.Append(new Rune(next).ToString());
            return sb.ToString();
        }
        return null;
    }
}
