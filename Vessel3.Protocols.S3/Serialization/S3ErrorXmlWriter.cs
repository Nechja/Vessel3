using System.Security;
using System.Text;

namespace Vessel3.Server.S3;

internal sealed class S3ErrorXmlWriter : IS3ErrorXmlWriter
{
    public Task WriteError(Stream output, Error error, string resource, string requestId, CancellationToken ct)
    {
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><Error><Code>{SecurityElement.Escape(error.Code)}</Code><Message>{SecurityElement.Escape(error.Message)}</Message><Resource>{SecurityElement.Escape(resource)}</Resource><RequestId>{SecurityElement.Escape(requestId)}</RequestId></Error>""";
        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
    }
}
