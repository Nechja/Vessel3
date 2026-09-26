namespace Vessel3.Server.S3;

internal interface IS3ErrorXmlWriter
{
    Task WriteError(Stream output, Error error, string resource, string requestId, CancellationToken ct);
}
