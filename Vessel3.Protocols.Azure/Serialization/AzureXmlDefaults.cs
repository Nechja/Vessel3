using System.Globalization;
using System.Text;
using System.Xml;

namespace Vessel3.Protocols.Azure.Serialization;

internal static class AzureXmlDefaults
{
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

    public static string ToRfc1123(DateTimeOffset dto) =>
        dto.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);

    public static string ToIso8601Ms(DateTimeOffset dto) =>
        dto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}
