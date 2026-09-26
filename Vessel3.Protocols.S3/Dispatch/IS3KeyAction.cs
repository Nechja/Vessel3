namespace Vessel3.Server.S3;

internal enum S3KeyHeaderFlag
{
    None,
    CopySource,
}

internal readonly record struct S3KeyRoute(string Method, S3KeySubresource Subresource, S3KeyHeaderFlag HeaderFlag = S3KeyHeaderFlag.None);

internal interface IS3KeyAction
{
    S3KeyRoute Route { get; }
    Task<IResult> Invoke(string bucket, string key, HttpContext ctx);
}
