using System.Collections.Frozen;

namespace Vessel3.Server.S3;

internal interface IS3BucketActionDispatcher
{
    Task<IResult> Dispatch(string method, string bucket, HttpContext ctx);
}

internal sealed class S3BucketActionDispatcher(IEnumerable<IS3BucketAction> actions, IBucketRegistry? registry, IHttpResultMapper http) : IS3BucketActionDispatcher
{
    private readonly FrozenDictionary<S3BucketRoute, Entry> table = actions.ToFrozenDictionary(a => a.Route, a => new Entry(a, a.GetType().Name));

    private readonly record struct Entry(IS3BucketAction Action, string Name);

    public Task<IResult> Dispatch(string method, string bucket, HttpContext ctx)
    {
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            if (registry is not null && registry.GetAccess(bucket).TryGetValue(out var access, out _) && access.ReadOnly)
                return Task.FromResult(http.Map(new BucketIsReadOnlyError(bucket)));
        }

        var sub = S3BucketSubresourceParser.From(ctx.Request.Query);

        return table.TryGetValue(new S3BucketRoute(method, sub), out var entry) ? Invoke(entry, bucket, ctx)
            : sub is not S3BucketSubresource.None && table.TryGetValue(new S3BucketRoute(method, S3BucketSubresource.None), out var fallback) ? Invoke(fallback, bucket, ctx)
            : Task.FromResult(http.Map(new MethodNotAllowedError($"{method} on bucket with subresource {sub}")));
    }

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
