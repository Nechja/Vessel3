using System.Security.Cryptography;
using System.Text;

namespace Vessel3.Primitives;

public static class CryptographicCompare
{
    public static bool FixedTimeEquals(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        var aLen = Encoding.UTF8.GetByteCount(a);
        var bLen = Encoding.UTF8.GetByteCount(b);
        if (aLen != bLen)
        {
            return false;
        }

        if (aLen > 512)
        {
            var aArr = new byte[aLen];
            var bArr = new byte[bLen];
            Encoding.UTF8.GetBytes(a, aArr);
            Encoding.UTF8.GetBytes(b, bArr);
            return CryptographicOperations.FixedTimeEquals(aArr, bArr);
        }

        Span<byte> aBytes = stackalloc byte[aLen];
        Span<byte> bBytes = stackalloc byte[bLen];
        Encoding.UTF8.GetBytes(a, aBytes);
        Encoding.UTF8.GetBytes(b, bBytes);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
