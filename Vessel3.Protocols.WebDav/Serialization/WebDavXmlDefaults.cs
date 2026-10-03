using System.Globalization;
using System.Text;
using System.Xml;

namespace Vessel3.Protocols.WebDav.Serialization;

internal static class WebDavXmlDefaults
{
    public const string DavNamespace = "DAV:";
    public const string DavPrefix = "D";

    public static XmlWriterSettings WriterSettings { get; } = new()
    {
        Async = true,
        Indent = false,
        OmitXmlDeclaration = false,
        Encoding = new UTF8Encoding(false),
    };

    public static string ToRfc1123(DateTimeOffset dto) =>
        dto.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);

    public static string ToIso8601(DateTimeOffset dto) =>
        dto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
