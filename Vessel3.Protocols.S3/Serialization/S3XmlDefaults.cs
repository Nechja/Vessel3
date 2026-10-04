using System.Text;
using System.Xml;

namespace Vessel3.Server.S3;

internal static class S3XmlDefaults
{
    public const string S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";
    public const string Iso8601Ms = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public static XmlWriterSettings WriterSettings { get; } = new()
    {
        Async = true,
        Indent = false,
        OmitXmlDeclaration = false,
        Encoding = new UTF8Encoding(false),
    };

    public static XmlReaderSettings ReaderSettings { get; } = new()
    {
        Async = true,
        IgnoreWhitespace = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    public static bool IsUrlEncoding(string? type) =>
        string.Equals(type, "url", StringComparison.OrdinalIgnoreCase);

    public static string Encode(string raw, bool urlEncode) =>
        urlEncode ? Uri.EscapeDataString(raw) : raw;

    public static string ModeToWire(RetentionMode m) => m switch
    {
        RetentionMode.Governance => "GOVERNANCE",
        RetentionMode.Compliance => "COMPLIANCE",
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    public static bool TryParseMode(string raw, out RetentionMode mode)
    {
        switch (raw)
        {
            case "GOVERNANCE":
                mode = RetentionMode.Governance;
                return true;
            case "COMPLIANCE":
                mode = RetentionMode.Compliance;
                return true;
            default:
                mode = default;
                return false;
        }
    }
}
