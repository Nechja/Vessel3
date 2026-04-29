namespace Vessel3.Server;

// Single point of truth for Error → HTTP status translation.
// Handlers do `Result<X>.Failure f => http.Map(f.Error)` and never inline a status code.
internal sealed class HttpResultMapper
{
    public IResult Map(Error error) => Results.StatusCode(StatusFor(error));

    public int StatusFor(Error error) => error switch
    {
        NotFoundError                       => 404,
        InvalidPathError                    => 400,
        BucketNotEmptyError                 => 409,
        MissingSecurityHeaderError          => 400,
        AuthorizationHeaderMalformedError   => 400,
        InvalidAccessKeyIdError             => 403,
        SignatureDoesNotMatchError          => 403,
        RequestTimeTooSkewedError           => 403,
        _                                   => 500,
    };
}
