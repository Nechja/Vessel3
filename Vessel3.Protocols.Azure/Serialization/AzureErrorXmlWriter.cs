using System.Security;
using System.Text;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;

namespace Vessel3.Protocols.Azure.Serialization;

internal interface IAzureErrorXmlWriter
{
    (string Code, int StatusCode) MapError(Error error);
    Task WriteErrorAsync(Stream output, Error error, string requestId, CancellationToken ct);
}

internal sealed record InvalidResourceNameError(string Detail) : Error("InvalidResourceName", Detail)
{
    public override int Status => 400;
}

internal sealed class AzureErrorXmlWriter : IAzureErrorXmlWriter
{
    public (string Code, int StatusCode) MapError(Error error) => error switch
    {
        NoSuchBucketError => ("ContainerNotFound", StatusCodes.Status404NotFound),
        NoSuchKeyError => ("BlobNotFound", StatusCodes.Status404NotFound),
        BucketNotEmptyError => ("ContainerNotEmpty", StatusCodes.Status409Conflict),
        PreconditionFailedError => ("ConditionNotMet", StatusCodes.Status412PreconditionFailed),
        BadDigestError => ("Md5Mismatch", StatusCodes.Status400BadRequest),
        InvalidBucketNameError => ("InvalidResourceName", StatusCodes.Status400BadRequest),
        InvalidPathError => ("InvalidResourceName", StatusCodes.Status400BadRequest),
        InvalidResourceNameError => ("InvalidResourceName", StatusCodes.Status400BadRequest),
        InvalidAccessKeyIdError => ("AuthenticationFailed", StatusCodes.Status403Forbidden),
        SignatureDoesNotMatchError => ("AuthenticationFailed", StatusCodes.Status403Forbidden),
        AccessDeniedError => ("AuthorizationFailure", StatusCodes.Status403Forbidden),
        BucketIsReadOnlyError => ("AccountIsDisabled", StatusCodes.Status403Forbidden),
        MethodNotAllowedError => ("UnsupportedHttpVerb", StatusCodes.Status405MethodNotAllowed),
        _ => (MapCodeString(error.Code), error.Status > 0 ? error.Status : StatusCodes.Status500InternalServerError)
    };

    private static string MapCodeString(string code) => code switch
    {
        "BucketAlreadyExists" => "ContainerAlreadyExists",
        "BucketAlreadyOwnedByYou" => "ContainerAlreadyExists",
        "NoSuchBucket" => "ContainerNotFound",
        "NoSuchKey" => "BlobNotFound",
        "BadDigest" => "Md5Mismatch",
        "PreconditionFailed" => "ConditionNotMet",
        _ => string.IsNullOrEmpty(code) ? "InternalError" : code
    };

    public Task WriteErrorAsync(Stream output, Error error, string requestId, CancellationToken ct)
    {
        var (code, _) = MapError(error);
        var time = DateTimeOffset.UtcNow.ToString("o");
        var authDetailElement = error is Vessel3.Protocols.Azure.Auth.AzureAuthenticationError authErr
            ? $"\n  <AuthenticationErrorDetail>{SecurityElement.Escape(authErr.Detail)}</AuthenticationErrorDetail>"
            : "";
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Error>
              <Code>{SecurityElement.Escape(code)}</Code>
              <Message>{SecurityElement.Escape(error.Message)}
            RequestId:{requestId}
            Time:{time}</Message>{authDetailElement}
            </Error>
            """;
        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
    }
}

internal sealed class AzureErrorResult(Error error, IAzureErrorXmlWriter xml) : IResult
{
    public async Task ExecuteAsync(HttpContext ctx)
    {
        var (code, status) = xml.MapError(error);
        var requestId = ctx.Items.TryGetValue("AzureRequestId", out var r) && r is string reqId
            ? reqId
            : Guid.NewGuid().ToString();

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/xml";
        ctx.Response.Headers["x-ms-error-code"] = code;
        ctx.Response.Headers["x-ms-request-id"] = requestId;

        await xml.WriteErrorAsync(ctx.Response.Body, error, requestId, ctx.RequestAborted);
    }
}
