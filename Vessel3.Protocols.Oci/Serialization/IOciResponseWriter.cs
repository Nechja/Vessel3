using Microsoft.AspNetCore.Http;

namespace Vessel3.Protocols.Oci.Serialization;

internal interface IOciResponseWriter
{
    Task WriteOciError(HttpContext ctx, int statusCode, string code, string message);
    string CleanSha(string digest);
}
