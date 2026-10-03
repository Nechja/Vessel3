using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Vessel3.Protocols.Oci.Serialization;

internal static class OciResponseWriter
{
    public static async Task WriteOciError(HttpContext ctx, int statusCode, string code, string message)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = OciMediaTypes.Json;
        var errDto = new OciErrorResponseDto([new OciErrorItemDto(code, message, null)]);
        await JsonSerializer.SerializeAsync(ctx.Response.Body, errDto, OciJsonContext.Default.OciErrorResponseDto);
    }

    public static string CleanSha(string digest) =>
        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest[7..].ToLowerInvariant()
            : digest.ToLowerInvariant();
}
