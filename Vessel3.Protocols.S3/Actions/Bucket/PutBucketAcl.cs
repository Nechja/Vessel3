
namespace Vessel3.Server.S3.Bucket;

internal sealed class PutBucketAcl(IBucketRegistry registry, IS3XmlReader reader, IHttpResultMapper http) : IS3BucketAction
{
    public S3BucketRoute Route => new(HttpMethods.Put, S3BucketSubresource.Acl);

    public async Task<IResult> Invoke(string bucket, HttpContext ctx)
    {
        var existingResult = registry.GetAccess(bucket);
        if (!existingResult.TryGetValue(out var currentAccess, out var getErr))
            return http.Map(getErr);

        bool publicRead;
        var aclHeader = ctx.Request.Headers["x-amz-acl"].ToString();
        if (!string.IsNullOrEmpty(aclHeader))
        {
            if (aclHeader.Equals("public-read", StringComparison.OrdinalIgnoreCase))
                publicRead = true;
            else if (aclHeader.Equals("private", StringComparison.OrdinalIgnoreCase))
                publicRead = false;
            else
                return http.Map(new InvalidArgumentError($"Unsupported canned ACL: {aclHeader}"));
        }
        else
        {
            var bodyResult = await reader.ReadAccessControlPolicy(ctx.Request.Body, ctx.RequestAborted);
            if (!bodyResult.TryGetValue(out publicRead, out var readErr))
                return http.Map(readErr);
        }

        var newAccess = currentAccess with { PublicRead = publicRead };
        return registry.SetAccess(bucket, newAccess).Match<IResult>(() => Results.Ok(), http.Map);
    }
}
