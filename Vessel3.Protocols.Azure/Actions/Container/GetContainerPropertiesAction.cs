using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Actions.Container;

internal sealed class GetContainerPropertiesAction(
    IBucketRegistry registry,
    IAzureErrorXmlWriter errorXml) : IAzureAction
{
    public AzureOperationKind Operation => AzureOperationKind.GetContainerProperties;
    public BucketCapability RequiredCapability => BucketCapability.Read;

    public Task<IResult> Execute(AzureRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Container))
        {
            return Task.FromResult<IResult>(new AzureErrorResult(new InvalidResourceNameError("Container name is required"), errorXml));
        }

        if (registry.EnsureExists(target.Container).TryGetError(out var err))
        {
            return Task.FromResult<IResult>(new AzureErrorResult(err, errorXml));
        }

        var now = DateTimeOffset.UtcNow;
        ctx.Response.Headers.ETag = $"\"{target.Container}\"";
        ctx.Response.Headers.LastModified = AzureXmlDefaults.ToRfc1123(now);
        ctx.Response.Headers["x-ms-lease-status"] = "unlocked";
        ctx.Response.Headers["x-ms-lease-state"] = "available";
        ctx.Response.Headers["x-ms-has-immutability-policy"] = "false";
        ctx.Response.Headers["x-ms-has-legal-hold"] = "false";
        ctx.Response.Headers["x-ms-default-encryption-scope"] = "$account-encryption-key";
        ctx.Response.Headers["x-ms-deny-encryption-scope-override"] = "false";

        if (registry.GetAccess(target.Container) is Result<BucketAccess?>.Success { Value: { PublicRead: true } })
        {
            ctx.Response.Headers["x-ms-blob-public-access"] = "container";
        }

        return Task.FromResult(Results.Ok());
    }
}
