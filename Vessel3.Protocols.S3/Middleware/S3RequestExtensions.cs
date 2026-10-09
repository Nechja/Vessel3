using Microsoft.Extensions.Primitives;

namespace Vessel3.Server.S3;

internal static class S3RequestExtensions
{
    public static string? Nullify(string? s) =>
        string.IsNullOrEmpty(s) ? null : s;

    public static string? Nullify(StringValues sv) =>
        StringValues.IsNullOrEmpty(sv) ? null : sv.ToString();

    public static string? VersionId(this HttpContext ctx) =>
        ctx.Request.Query.TryGetValue("versionId", out var v) ? Nullify(v) : null;

    public static string? UploadId(this HttpContext ctx) =>
        ctx.Request.Query.TryGetValue("uploadId", out var v) ? Nullify(v) : null;

    public static bool BypassGovernanceRetention(this HttpContext ctx) =>
        ctx.Request.Headers.TryGetValue("x-amz-bypass-governance-retention", out var v)
        && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    public static string? CurrentVersionOf(this IBucketRegistry registry, string bucket, string key) =>
        registry.GetCurrentPut(bucket, key).TryGetValue(out var cur, out _) ? cur?.VersionId : null;

    public static CallerIdentity? GetCaller(this HttpContext ctx) =>
        ctx.Items.TryGetValue("CallerIdentity", out var obj) && obj is CallerIdentity caller ? caller : null;

    public static void SetCaller(this HttpContext ctx, CallerIdentity caller) =>
        ctx.Items["CallerIdentity"] = caller;
}
