using System.Collections.Frozen;
using Vessel3.Storage;

namespace Vessel3.Server.S3;

internal interface IS3BucketActionDispatcher
{
    Task<IResult> Dispatch(string method, string bucket, HttpContext ctx);
}

internal sealed class S3BucketActionDispatcher(
    IEnumerable<IS3BucketAction> actions,
    IBucketRegistry? registry,
    IHttpResultMapper http,
    IS3SubresourceResolver? subresourceResolver = null) : IS3BucketActionDispatcher
{
    private readonly FrozenDictionary<S3BucketRoute, Entry> table = actions.ToFrozenDictionary(a => a.Route, a => new Entry(a, a.GetType().Name));
    private readonly IS3SubresourceResolver subresourceResolver = subresourceResolver ?? new S3SubresourceResolver();

    private readonly record struct Entry(IS3BucketAction Action, string Name);

    public Task<IResult> Dispatch(string method, string bucket, HttpContext ctx)
    {
        var sub = subresourceResolver.ResolveBucket(ctx.Request.Query);

        if (!TryResolveEntry(method, sub, out var entry))
        {
            return Reject(new MethodNotAllowedError($"{method} on bucket with subresource {sub}"));
        }

        var caller = ctx.GetCaller();
        if (registry is not null && entry.Action is not Bucket.CreateBucket)
        {
            if (caller is not null)
            {
                var capability = ResolveRequiredCapability(method, sub);
                if (registry.AuthorizeAccess(bucket, caller, capability) is Result.Failure f)
                {
                    return Reject(f.Error);
                }
            }
            else if (IsBlockedByReadOnlyAccess(method, bucket))
            {
                return Reject(new BucketIsReadOnlyError(bucket));
            }
        }

        return Invoke(entry, bucket, ctx);
    }

    private static BucketCapability ResolveRequiredCapability(string method, S3BucketSubresource sub) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
            ? BucketCapability.Read
            : sub is S3BucketSubresource.Delete
                ? BucketCapability.Write
                : BucketCapability.Admin;

    private Task<IResult> Reject(Error error) => Task.FromResult(http.Map(error));

    private bool TryResolveEntry(string method, S3BucketSubresource sub, out Entry entry) =>
        table.TryGetValue(new S3BucketRoute(method, sub), out entry)
        || (sub is not S3BucketSubresource.None && table.TryGetValue(new S3BucketRoute(method, S3BucketSubresource.None), out entry));

    private bool IsBlockedByReadOnlyAccess(string method, string bucket) =>
        !HttpMethods.IsGet(method)
        && !HttpMethods.IsHead(method)
        && registry is not null
        && registry.GetAccess(bucket) is Result<BucketAccess>.Success { Value.ReadOnly: true };

    private static async Task<IResult> Invoke(Entry entry, string bucket, HttpContext ctx)
    {
        var trace = RequestTrace.Current;
        if (trace is not null)
        {
            trace.Action = entry.Name;
            trace.Bucket = bucket;
        }
        var result = await entry.Action.Invoke(bucket, ctx);
        trace?.MarkHandled();
        return result;
    }
}
