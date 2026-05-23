namespace Vessel3.Server;

internal abstract record Error(string Code, string Message)
{
    public abstract int Status { get; }
}

internal sealed record NotFoundError(string Resource)
    : Error("NotFound", $"{Resource} not found")
{ public override int Status => 404; }

internal sealed record NoSuchBucketError(string Bucket)
    : Error("NoSuchBucket", $"The specified bucket does not exist: {Bucket}")
{ public override int Status => 404; }

internal sealed record NoSuchKeyError(string Key)
    : Error("NoSuchKey", $"The specified key does not exist: {Key}")
{ public override int Status => 404; }

internal sealed record NoSuchVersionError(string Key, string VersionId)
    : Error("NoSuchVersion", $"The specified version does not exist: {Key}@{VersionId}")
{ public override int Status => 404; }

internal sealed record InvalidPathError(string Detail)
    : Error("InvalidPath", Detail)
{ public override int Status => 400; }

internal sealed record InvalidBucketNameError(string Bucket)
    : Error("InvalidBucketName", $"The specified bucket is not valid: {Bucket}")
{ public override int Status => 400; }

internal sealed record BucketNotEmptyError(string Bucket)
    : Error("BucketNotEmpty", $"Bucket {Bucket} is not empty")
{ public override int Status => 409; }

internal sealed record BadDigestError(string Detail)
    : Error("BadDigest", $"Content does not match declared hash: {Detail}")
{ public override int Status => 400; }

internal sealed record PreconditionFailedError(string Resource)
    : Error("PreconditionFailed", $"At least one of the preconditions failed for {Resource}")
{ public override int Status => 412; }

internal sealed record MalformedXmlError(string Detail)
    : Error("MalformedXML", $"Could not parse XML body: {Detail}")
{ public override int Status => 400; }

internal sealed record MissingSecurityHeaderError(string Header)
    : Error("MissingSecurityHeader", $"Required header {Header} is missing")
{ public override int Status => 400; }

internal sealed record AuthorizationHeaderMalformedError(string Detail)
    : Error("AuthorizationHeaderMalformed", Detail)
{ public override int Status => 400; }

internal sealed record InvalidAccessKeyIdError(string AccessKey)
    : Error("InvalidAccessKeyId", $"Access key {AccessKey} not recognised")
{ public override int Status => 403; }

internal sealed record SignatureDoesNotMatchError()
    : Error("SignatureDoesNotMatch", "The request signature we calculated does not match the signature you provided")
{ public override int Status => 403; }

internal sealed record RequestTimeTooSkewedError()
    : Error("RequestTimeTooSkewed", "The request timestamp is outside the allowed skew window")
{ public override int Status => 403; }

internal sealed record NoSuchUploadError(string UploadId)
    : Error("NoSuchUpload", $"Upload {UploadId} not found")
{ public override int Status => 404; }

internal sealed record InvalidPartError(string Detail)
    : Error("InvalidPart", Detail)
{ public override int Status => 400; }

internal sealed record InvalidPartOrderError(string Detail)
    : Error("InvalidPartOrder", Detail)
{ public override int Status => 400; }

internal sealed record EntityTooSmallError(int PartNumber, long Actual, long Minimum)
    : Error("EntityTooSmall", $"part {PartNumber} is {Actual} bytes; minimum is {Minimum}")
{ public override int Status => 400; }

internal sealed record InsufficientStorageError(string Detail)
    : Error("InternalError", $"Storage full: {Detail}")
{ public override int Status => 507; }

internal sealed record InvalidTagError(string Detail)
    : Error("InvalidTag", Detail)
{ public override int Status => 400; }

internal sealed record MethodNotAllowedError(string Detail)
    : Error("MethodNotAllowed", Detail)
{ public override int Status => 405; }

internal sealed record InvalidBucketStateError(string Detail)
    : Error("InvalidBucketState", Detail)
{ public override int Status => 409; }

internal sealed record ObjectLockConfigurationNotFoundErrorResult(string Bucket)
    : Error("ObjectLockConfigurationNotFoundError", $"Object Lock configuration not found for bucket {Bucket}")
{ public override int Status => 404; }

internal sealed record NoSuchObjectLockConfigurationError(string Resource)
    : Error("NoSuchObjectLockConfiguration", $"No object lock configuration for {Resource}")
{ public override int Status => 404; }

internal sealed record AccessDeniedError(string Detail)
    : Error("AccessDenied", Detail)
{ public override int Status => 403; }

internal abstract record Result<T>
{
    public abstract TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure);

    internal sealed record Success(T Value) : Result<T>
    {
        public override TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure) => onSuccess(Value);
    }

    internal sealed record Failure(Error Error) : Result<T>
    {
        public override TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure) => onFailure(Error);
    }

    public static implicit operator Result<T>(T value) => new Success(value);
    public static implicit operator Result<T>(Error error) => new Failure(error);
}
