using Vessel3.Server.Storage;
using static Vessel3.Server.RequestHelpers;

namespace Vessel3.Server.S3;

internal static class S3RequestExtensions
{
    public static string? VersionId(this HttpContext ctx) =>
        Nullify(ctx.Request.Query["versionId"].ToString());

    public static string? UploadId(this HttpContext ctx) =>
        Nullify(ctx.Request.Query["uploadId"].ToString());

    public static bool BypassGovernanceRetention(this HttpContext ctx) =>
        ctx.Request.Headers["x-amz-bypass-governance-retention"].ToString()
            .Equals("true", StringComparison.OrdinalIgnoreCase);

    public static string? CurrentVersionOf(this IBucketRegistry registry, string bucket, string key) =>
        registry.GetCurrentPut(bucket, key).TryGetValue(out var cur, out _) ? cur?.VersionId : null;
}
