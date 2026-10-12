namespace Vessel3.Protocols.Azure;

internal static class AzureHeaders
{
    public const string RequestId = "x-ms-request-id";
    public const string Version = "x-ms-version";
    public const string ClientRequestId = "x-ms-client-request-id";
    public const string Date = "x-ms-date";
    public const string BlobType = "x-ms-blob-type";
    public const string BlobContentType = "x-ms-blob-content-type";
    public const string BlobContentLength = "x-ms-blob-content-length";
    public const string BlobContentMd5 = "x-ms-blob-content-md5";
    public const string UserMetaPrefix = "x-ms-meta-";
    public const string SharedKeyPrefix = "SharedKey ";
}

internal static class AzureMediaTypes
{
    public const string Xml = "application/xml";
    public const string OctetStream = "application/octet-stream";
}
