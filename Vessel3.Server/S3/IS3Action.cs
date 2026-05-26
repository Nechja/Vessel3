namespace Vessel3.Server.S3;

internal readonly record struct S3Route(string Method, S3Subresource Subresource);

internal interface IS3Action
{
    S3Route Route { get; }
    Task<IResult> Invoke(string bucket, HttpContext ctx);
}
