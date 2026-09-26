namespace Vessel3.Server.S3;

internal readonly record struct S3BucketRoute(string Method, S3BucketSubresource Subresource);

internal interface IS3BucketAction
{
    S3BucketRoute Route { get; }
    Task<IResult> Invoke(string bucket, HttpContext ctx);
}
