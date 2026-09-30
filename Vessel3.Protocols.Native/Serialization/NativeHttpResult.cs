using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;

namespace Vessel3.Protocols.Native.Serialization;

public static class NativeHttpResult
{
    public static IResult ToHttpResult(this Error error) =>
        Results.Json(new ErrorDto(error.Code, error.Message), NativeJsonContext.Default.ErrorDto, statusCode: error.Status);
}
