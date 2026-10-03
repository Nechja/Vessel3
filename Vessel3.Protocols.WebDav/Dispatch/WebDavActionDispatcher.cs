using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.WebDav.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav.Dispatch;

internal interface IWebDavActionDispatcher
{
    Task<IResult> Dispatch(WebDavRequestTarget target, HttpContext ctx);
}

internal sealed class WebDavActionDispatcher(
    IEnumerable<IWebDavAction> actions,
    IBucketRegistry registry) : IWebDavActionDispatcher
{
    private readonly FrozenDictionary<WebDavOperationKind, IWebDavAction> actions = actions.ToFrozenDictionary(a => a.Operation);

    public async Task<IResult> Dispatch(WebDavRequestTarget target, HttpContext ctx)
    {
        if (target.Operation is WebDavOperationKind.Unknown || !actions.TryGetValue(target.Operation, out var action))
        {
            return new WebDavErrorResult(new MethodNotAllowedError($"Method {ctx.Request.Method} not supported on {ctx.Request.Path}"));
        }

        var caller = ctx.Items.TryGetValue("CallerIdentity", out var c) && c is CallerIdentity ci
            ? ci
            : CallerIdentity.System;

        if (!string.IsNullOrEmpty(target.Bucket) && RequiresBucketAuthorization(target))
        {
            var capability = ResolveCapability(target.Operation);
            if (registry.AuthorizeAccess(target.Bucket, caller, capability) is Result.Failure f)
            {
                return new WebDavErrorResult(f.Error);
            }
        }

        return await action.Execute(target, ctx);
    }

    private static BucketCapability ResolveCapability(WebDavOperationKind op) => op switch
    {
        WebDavOperationKind.Options or
        WebDavOperationKind.Propfind or
        WebDavOperationKind.Get or
        WebDavOperationKind.Head => BucketCapability.Read,

        WebDavOperationKind.Put or
        WebDavOperationKind.Delete or
        WebDavOperationKind.Mkcol or
        WebDavOperationKind.Move or
        WebDavOperationKind.Copy or
        WebDavOperationKind.Lock or
        WebDavOperationKind.Unlock or
        WebDavOperationKind.Proppatch => BucketCapability.Write,

        _ => BucketCapability.Admin
    };

    private static bool RequiresBucketAuthorization(WebDavRequestTarget target) =>
        !(target.Operation is WebDavOperationKind.Mkcol && string.IsNullOrEmpty(target.Path));
}
