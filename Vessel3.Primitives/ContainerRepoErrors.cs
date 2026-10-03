namespace Vessel3.Primitives;

public sealed record NoSuchContainerRepoError(string Repo)
    : Error("NAME_UNKNOWN", $"repository name not known to registry: {Repo}")
{ public override int Status => 404; }

public sealed record NoSuchManifestError(string Repo, string Reference)
    : Error("MANIFEST_UNKNOWN", $"manifest unknown to registry: {Reference} in {Repo}")
{ public override int Status => 404; }

public sealed record NoSuchBlobError(string Digest)
    : Error("BLOB_UNKNOWN", $"blob unknown to registry: {Digest}")
{ public override int Status => 404; }

public sealed record BlobUploadInvalidError(string Detail)
    : Error("BLOB_UPLOAD_INVALID", Detail)
{ public override int Status => 400; }

public sealed record BlobUploadUnknownError(string UploadId)
    : Error("BLOB_UPLOAD_UNKNOWN", $"blob upload unknown: {UploadId}")
{ public override int Status => 404; }

public sealed record ManifestInvalidError(string Detail)
    : Error("MANIFEST_INVALID", Detail)
{ public override int Status => 400; }

public sealed record UnsupportedMediaTypeError(string MediaType)
    : Error("UNSUPPORTED", $"media type not supported: {MediaType}")
{ public override int Status => 415; }

public sealed record OciDeniedError(string Detail = "access denied")
    : Error("DENIED", Detail)
{ public override int Status => 403; }

public sealed record OciUnauthorizedError(string Detail = "authentication required")
    : Error("UNAUTHORIZED", Detail)
{ public override int Status => 401; }
