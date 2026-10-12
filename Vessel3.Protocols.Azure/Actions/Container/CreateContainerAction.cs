using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Actions.Container;

internal sealed class CreateContainerAction(
    IBucketRegistry registry,
    IAzureErrorXmlWriter errorXml) : IAzureAction
{
    public AzureOperationKind Operation => AzureOperationKind.CreateContainer;

    public Task<IResult> Execute(AzureRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Container))
        {
            return Task.FromResult<IResult>(new AzureErrorResult(new InvalidResourceNameError("Container name is required"), errorXml));
        }

        var caller = ctx.Items.TryGetValue("CallerIdentity", out var c) && c is CallerIdentity ci
            ? ci
            : CallerIdentity.System;

        var createResult = registry.Create(target.Container, caller);
        if (!createResult.TryGetValue(out _, out var err))
        {
            return Task.FromResult<IResult>(new AzureErrorResult(err, errorXml));
        }

        if (ctx.Request.Headers.TryGetValue("x-ms-blob-public-access", out var pa) && pa.Count > 0 && pa[0] is { Length: > 0 } publicAccess &&
            (string.Equals(publicAccess, "container", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(publicAccess, "blob", StringComparison.OrdinalIgnoreCase)))
        {
            registry.SetAccess(target.Container, new BucketAccess(PublicRead: true, ReadOnly: false));
        }

        var now = DateTimeOffset.UtcNow;
        ctx.Response.Headers.ETag = $"\"{target.Container}\"";
        ctx.Response.Headers.LastModified = AzureXmlDefaults.ToRfc1123(now);

        return Task.FromResult(Results.StatusCode(StatusCodes.Status201Created));
    }
}
