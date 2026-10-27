#if VESSEL3_UI
using System.Security.Cryptography;
using System.Text;

namespace Vessel3.Server.Ui;

internal static class UiEndpoints
{
    private const string BlazorFrameworkPrefix = "_framework/";
    private const string BlazorContentPrefix = "_content/";

    public static bool BasicAuthOk(string authorization, string accessKey, string secretKey)
    {
        const string scheme = "Basic ";
        if (!authorization.StartsWith(scheme, StringComparison.Ordinal)) return false;

        var b64Span = authorization.AsSpan(scheme.Length).Trim();
        Span<byte> rawBytes = stackalloc byte[512];
        if (!Convert.TryFromBase64Chars(b64Span, rawBytes, out var bytesWritten)) return false;

        var decoded = rawBytes[..bytesWritten];
        var colon = decoded.IndexOf((byte)':');
        if (colon < 0) return false;

        var user = decoded[..colon];
        var pass = decoded[(colon + 1)..];

        var userLen = Encoding.UTF8.GetByteCount(accessKey);
        var passLen = Encoding.UTF8.GetByteCount(secretKey);
        if (userLen > 256 || passLen > 256) return false;

        Span<byte> expectedUser = stackalloc byte[userLen];
        Span<byte> expectedPass = stackalloc byte[passLen];
        Encoding.UTF8.GetBytes(accessKey, expectedUser);
        Encoding.UTF8.GetBytes(secretKey, expectedPass);

        return CryptographicOperations.FixedTimeEquals(user, expectedUser) &
               CryptographicOperations.FixedTimeEquals(pass, expectedPass);
    }

    public static bool IsAssetPath(string rel) =>
        rel.StartsWith(BlazorFrameworkPrefix, StringComparison.Ordinal)
        || rel.StartsWith(BlazorContentPrefix, StringComparison.Ordinal)
        || (!rel.Contains('/', StringComparison.Ordinal) && rel.Contains('.', StringComparison.Ordinal));
}
#endif
