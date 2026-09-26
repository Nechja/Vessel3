using System.Xml;

namespace Vessel3.Server.S3;

internal sealed class S3ErrorXmlWriter : IS3ErrorXmlWriter
{
    public async Task WriteError(Stream output, Error error, string resource, string requestId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var w = XmlWriter.Create(output, S3XmlDefaults.WriterSettings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "Error", null);
        await w.WriteElementStringAsync(null, "Code", null, error.Code);
        await w.WriteElementStringAsync(null, "Message", null, error.Message);
        await w.WriteElementStringAsync(null, "Resource", null, resource);
        await w.WriteElementStringAsync(null, "RequestId", null, requestId);
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }
}
