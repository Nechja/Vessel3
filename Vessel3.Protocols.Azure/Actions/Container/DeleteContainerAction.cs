using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Azure.Dispatch;
using Vessel3.Protocols.Azure.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Actions.Container;

internal sealed class DeleteContainerAction(
    IBucketRegistry registry,
    IAzureErrorXmlWriter errorXml) : IAzureAction
{
    public AzureOperationKind Operation => AzureOperationKind.DeleteContainer;

    public Task<IResult> Execute(AzureRequestTarget target, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(target.Container))
        {
            return Task.FromResult<IResult>(new AzureErrorResult(new InvalidResourceNameError("Container name is required"), errorXml));
        }

        var caller = ctx.Items.TryGetValue("CallerIdentity", out var c) && c is CallerIdentity ci
            ? ci
            : CallerIdentity.System;

        var deleteResult = registry.Delete(target.Container, caller);
        return Task.FromResult<IResult>(
            deleteResult is Result.Failure f
                ? new AzureErrorResult(f.Error, errorXml)
                : Results.StatusCode(StatusCodes.Status202Accepted));
    }
}
