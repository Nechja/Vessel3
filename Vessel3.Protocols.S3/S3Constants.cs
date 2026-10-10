namespace Vessel3.Server.S3;

internal static class S3Headers
{
    public const string ContentSha256 = "x-amz-content-sha256";
    public const string ContentMd5 = "Content-MD5";
    public const string UserMetaPrefix = "x-amz-meta-";
    public const string CopySource = "x-amz-copy-source";
    public const string CopySourceIfMatch = "x-amz-copy-source-if-match";
    public const string CopySourceIfNoneMatch = "x-amz-copy-source-if-none-match";
    public const string CopySourceIfModifiedSince = "x-amz-copy-source-if-modified-since";
    public const string CopySourceIfUnmodifiedSince = "x-amz-copy-source-if-unmodified-since";
    public const string Trailer = "x-amz-trailer";
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";
    public const string IfMatch = "If-Match";
    public const string IfNoneMatch = "If-None-Match";
    public const string IfModifiedSince = "If-Modified-Since";
    public const string IfUnmodifiedSince = "If-Unmodified-Since";
}
