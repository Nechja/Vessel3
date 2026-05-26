namespace Vessel3.Server.S3;

internal readonly record struct S3KeyRoute(string Method, S3KeySubresource Subresource);

internal interface IS3KeyAction
{
    S3KeyRoute Route { get; }
    Task<IResult> Invoke(string bucket, string key, HttpContext ctx);
}
