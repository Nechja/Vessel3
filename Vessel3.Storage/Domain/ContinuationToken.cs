using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Vessel3.Storage;

internal static class ContinuationToken
{
    public static string Encode(string key)
    {
        var maxByteCount = Encoding.UTF8.GetMaxByteCount(key.Length);
        if (maxByteCount <= 512)
        {
            Span<byte> utf8 = stackalloc byte[maxByteCount];
            var written = Encoding.UTF8.GetBytes(key, utf8);
            return Convert.ToBase64String(utf8[..written]);
        }
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(key));
    }

    public static bool TryDecode(string token, [NotNullWhen(true)] out string? key)
    {
        key = null;
        if (string.IsNullOrEmpty(token)) return false;

        var maxByteCount = (token.Length * 3 + 3) / 4;
        if (maxByteCount <= 512)
        {
            Span<byte> buffer = stackalloc byte[maxByteCount];
            if (!Convert.TryFromBase64String(token, buffer, out var bytesWritten))
                return false;
            try
            {
                key = Encoding.UTF8.GetString(buffer[..bytesWritten]);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        try
        {
            var bytes = Convert.FromBase64String(token);
            key = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
