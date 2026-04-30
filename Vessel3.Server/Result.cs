namespace Vessel3.Server;

internal abstract record Error(string Code, string Message)
{
    public abstract int Status { get; }
}

internal sealed record NotFoundError(string Resource)
    : Error("NotFound", $"{Resource} not found")
{ public override int Status => 404; }

internal sealed record InvalidPathError(string Detail)
    : Error("InvalidPath", Detail)
{ public override int Status => 400; }

internal sealed record BucketNotEmptyError(string Bucket)
    : Error("BucketNotEmpty", $"Bucket {Bucket} is not empty")
{ public override int Status => 409; }

internal sealed record BadDigestError(string Detail)
    : Error("BadDigest", $"Content does not match declared hash: {Detail}")
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
