using System.Collections.Frozen;

namespace Vessel3.Server.S3;

internal interface IS3KeyActionDispatcher
{
    Task<IResult> Dispatch(string method, string bucket, string key, HttpContext ctx);
}

internal sealed class S3KeyActionDispatcher(
    IEnumerable<IS3KeyAction> actions,
    IBucketRegistry? registry,
    IHttpResultMapper http,
    IS3SubresourceResolver? subresourceResolver = null) : IS3KeyActionDispatcher
{
    private readonly FrozenDictionary<S3KeyRoute, Entry> table = actions.ToFrozenDictionary(a => a.Route, a => new Entry(a, a.GetType().Name));
    private readonly IS3SubresourceResolver subresourceResolver = subresourceResolver ?? new S3SubresourceResolver();

    private readonly record struct Entry(IS3KeyAction Action, string Name);

    public async Task<IResult> Dispatch(string method, string bucket, string key, HttpContext ctx)
    {
        if (IsBlockedByReadOnlyAccess(method, bucket))
        {
            return http.Map(new BucketIsReadOnlyError(bucket));
        }

        var sub = subresourceResolver.ResolveKey(ctx.Request.Query);
        var headerFlag = ResolveHeaderFlag(ctx.Request.Headers);

        return TryResolveEntry(method, sub, headerFlag, out var entry)
            ? await Invoke(entry, bucket, key, ctx)
            : http.Map(new MethodNotAllowedError($"{method} on key with subresource {sub} headerFlag {headerFlag}"));
    }

    private bool TryResolveEntry(string method, S3KeySubresource sub, S3KeyHeaderFlag headerFlag, out Entry entry) =>
        table.TryGetValue(new S3KeyRoute(method, sub, headerFlag), out entry)
        || (sub is not S3KeySubresource.None && table.TryGetValue(new S3KeyRoute(method, S3KeySubresource.None, headerFlag), out entry));

    private bool IsBlockedByReadOnlyAccess(string method, string bucket) =>
        !HttpMethods.IsGet(method)
        && !HttpMethods.IsHead(method)
        && registry is not null
        && registry.GetAccess(bucket) is Result<BucketAccess>.Success { Value.ReadOnly: true };

    private static S3KeyHeaderFlag ResolveHeaderFlag(IHeaderDictionary headers) =>
        !string.IsNullOrEmpty(headers["x-amz-copy-source"].ToString())
            ? S3KeyHeaderFlag.CopySource
            : S3KeyHeaderFlag.None;

    private static async Task<IResult> Invoke(Entry entry, string bucket, string key, HttpContext ctx)
    {
        var trace = RequestTrace.Current;
        if (trace is not null)
        {
            trace.Action = entry.Name;
            trace.Bucket = bucket;
            trace.Key = key;
        }
        var result = await entry.Action.Invoke(bucket, key, ctx);
        trace?.MarkHandled();
        return result;
    }
}
