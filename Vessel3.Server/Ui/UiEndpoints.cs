#if VESSEL3_UI
using System.Security.Cryptography;
using System.Text;

namespace Vessel3.Server.Ui;

internal static class UiEndpoints
{
    public static bool BasicAuthOk(string authorization, string accessKey, string secretKey)
    {
        const string scheme = "Basic ";
        if (!authorization.StartsWith(scheme, StringComparison.Ordinal)) return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[scheme.Length..]));
            var colon = decoded.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) return false;
            var userOk = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(decoded[..colon]), Encoding.UTF8.GetBytes(accessKey));
            var passOk = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(decoded[(colon + 1)..]), Encoding.UTF8.GetBytes(secretKey));
            return userOk & passOk;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsAssetPath(string rel) =>
        rel.StartsWith("_framework/", StringComparison.Ordinal)
        || rel.StartsWith("_content/", StringComparison.Ordinal)
        || (!rel.Contains('/', StringComparison.Ordinal) && rel.Contains('.', StringComparison.Ordinal));
}
#endif
