using System.Security;
using System.Text;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;

namespace Vessel3.Protocols.WebDav.Serialization;

internal sealed class WebDavErrorResult(Error error) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var statusCode = MapStatusCode(error);
        httpContext.Response.StatusCode = statusCode;

        if (statusCode == StatusCodes.Status401Unauthorized)
        {
            httpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"Vessel3 WebDAV\"";
        }

        httpContext.Response.Headers["DAV"] = "1, 2";
        httpContext.Response.Headers["MS-Author-Via"] = "DAV";

        if (!string.IsNullOrEmpty(error.Message))
        {
            httpContext.Response.ContentType = "application/xml; charset=utf-8";
            var xml = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <D:error xmlns:D="DAV:">
                  <D:message>{SecurityElement.Escape(error.Message)}</D:message>
                </D:error>
                """;
            await httpContext.Response.WriteAsync(xml, Encoding.UTF8, httpContext.RequestAborted);
        }
    }

    public static int MapStatusCode(Error error) => error switch
    {
        HttpError httpErr => httpErr.StatusCode,
        NoSuchBucketError or NoSuchKeyError or NotFoundError => StatusCodes.Status404NotFound,
        BucketNotEmptyError or InvalidBucketStateError => StatusCodes.Status409Conflict,
        PreconditionFailedError => StatusCodes.Status412PreconditionFailed,
        AccessDeniedError or InvalidAccessKeyIdError or SignatureDoesNotMatchError or BucketIsReadOnlyError => StatusCodes.Status403Forbidden,
        MethodNotAllowedError => StatusCodes.Status405MethodNotAllowed,
        InsufficientStorageError => StatusCodes.Status507InsufficientStorage,
        _ => error.Status > 0 ? error.Status : StatusCodes.Status400BadRequest
    };
}
