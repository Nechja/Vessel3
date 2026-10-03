namespace Vessel3.Protocols.Oci;

internal static class OciHeaders
{
    public const string DockerDistributionApiVersion = "Docker-Distribution-API-Version";
    public const string DockerContentDigest = "Docker-Content-Digest";
    public const string DockerUploadUuid = "Docker-Upload-UUID";
    public const string ApiVersionValue = "registry/2.0";
    public const string Location = "Location";
    public const string Range = "Range";
    public const string Link = "Link";
    public const string BasicPrefix = "Basic ";
}

internal static class OciMediaTypes
{
    public const string Json = "application/json";
    public const string OctetStream = "application/octet-stream";
    public const string ManifestV2 = "application/vnd.docker.distribution.manifest.v2+json";
}

internal static class OciErrorCodes
{
    public const string NameUnknown = "NAME_UNKNOWN";
    public const string BlobUnknown = "BLOB_UNKNOWN";
    public const string ManifestUnknown = "MANIFEST_UNKNOWN";
    public const string ManifestInvalid = "MANIFEST_INVALID";
    public const string DigestInvalid = "DIGEST_INVALID";
    public const string BlobUploadInvalid = "BLOB_UPLOAD_INVALID";
    public const string BlobUploadUnknown = "BLOB_UPLOAD_UNKNOWN";
    public const string InternalError = "INTERNAL_ERROR";
}
