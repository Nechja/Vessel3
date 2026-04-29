namespace Vessel3.Server;

internal abstract record Error(string Code, string Message);

internal sealed record NotFoundError(string Resource) : Error("NotFound", $"{Resource} not found");
internal sealed record InvalidPathError(string Detail) : Error("InvalidPath", Detail);
internal sealed record BucketNotEmptyError(string Bucket) : Error("BucketNotEmpty", $"Bucket {Bucket} is not empty");

internal sealed record MissingSecurityHeaderError(string Header) : Error("MissingSecurityHeader", $"Required header {Header} is missing");
internal sealed record AuthorizationHeaderMalformedError(string Detail) : Error("AuthorizationHeaderMalformed", Detail);
internal sealed record InvalidAccessKeyIdError(string AccessKey) : Error("InvalidAccessKeyId", $"Access key {AccessKey} not recognised");
internal sealed record SignatureDoesNotMatchError() : Error("SignatureDoesNotMatch", "The request signature we calculated does not match the signature you provided");
internal sealed record RequestTimeTooSkewedError() : Error("RequestTimeTooSkewed", "The request timestamp is outside the allowed skew window");

internal abstract record Result<T>
{
    internal sealed record Success(T Value) : Result<T>;
    internal sealed record Failure(Error Error) : Result<T>;

    public static implicit operator Result<T>(T value) => new Success(value);
    public static implicit operator Result<T>(Error error) => new Failure(error);
}
