using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Azure.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Azure.Dispatch;

internal interface IAzureActionDispatcher
{
    Task<IResult> Dispatch(AzureRequestTarget target, HttpContext ctx);
}

internal sealed class AzureActionDispatcher(
    IEnumerable<IAzureAction> actions,
    IBucketRegistry registry,
    IAzureErrorXmlWriter errorXml) : IAzureActionDispatcher
{
    private readonly FrozenDictionary<AzureOperationKind, IAzureAction> actions = actions.ToFrozenDictionary(a => a.Operation);

    public async Task<IResult> Dispatch(AzureRequestTarget target, HttpContext ctx)
    {
        if (target.Operation is AzureOperationKind.Unknown || !actions.TryGetValue(target.Operation, out var action))
        {
            return new AzureErrorResult(new MethodNotAllowedError($"Unsupported operation {ctx.Request.Method} on {ctx.Request.Path}"), errorXml);
        }

        var caller = ctx.Items.TryGetValue("CallerIdentity", out var c) && c is CallerIdentity ci
            ? ci
            : CallerIdentity.System;

        if (!string.IsNullOrEmpty(target.Container) && target.Operation is not AzureOperationKind.CreateContainer)
        {
            var capability = ResolveCapability(target.Operation);
            if (registry.AuthorizeAccess(target.Container, caller, capability) is Result.Failure f)
            {
                return new AzureErrorResult(f.Error, errorXml);
            }
        }

        return await action.Execute(target, ctx);
    }

    private static BucketCapability ResolveCapability(AzureOperationKind op) => op switch
    {
        AzureOperationKind.ListContainers or
        AzureOperationKind.GetContainerProperties or
        AzureOperationKind.GetContainerAcl or
        AzureOperationKind.GetContainerMetadata or
        AzureOperationKind.ListBlobs or
        AzureOperationKind.GetBlob or
        AzureOperationKind.HeadBlob or
        AzureOperationKind.GetBlobMetadata or
        AzureOperationKind.GetBlobTags or
        AzureOperationKind.GetBlockList => BucketCapability.Read,

        AzureOperationKind.PutBlob or
        AzureOperationKind.DeleteBlob or
        AzureOperationKind.CopyBlob or
        AzureOperationKind.PutBlock or
        AzureOperationKind.PutBlockList or
        AzureOperationKind.SetBlobMetadata or
        AzureOperationKind.PutBlobTags => BucketCapability.Write,

        _ => BucketCapability.Admin
    };
}
