using System.Collections.Frozen;
using Vessel3.Server.Storage;

namespace Vessel3.Server.S3;

internal interface IS3KeyActionDispatcher
{
    Task<IResult> Dispatch(string method, string bucket, string key, HttpContext ctx);
}

internal sealed class S3KeyActionDispatcher(IEnumerable<IS3KeyAction> actions, IBucketRegistry? registry, IHttpResultMapper http) : IS3KeyActionDispatcher
{
    public S3KeyActionDispatcher(IEnumerable<IS3KeyAction> actions, IHttpResultMapper http)
        : this(actions, null, http) { }

    private readonly FrozenDictionary<S3KeyRoute, Entry> table = actions.ToFrozenDictionary(a => a.Route, a => new Entry(a, a.GetType().Name));

    private readonly record struct Entry(IS3KeyAction Action, string Name);

    public Task<IResult> Dispatch(string method, string bucket, string key, HttpContext ctx)
    {
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            if (registry is not null && registry.GetAccess(bucket).TryGetValue(out var access, out _) && access.ReadOnly)
                return Task.FromResult(http.Map(new BucketIsReadOnlyError(bucket)));
        }

        var sub = S3KeySubresourceParser.From(ctx.Request.Query);
        var headerFlag = !string.IsNullOrEmpty(ctx.Request.Headers["x-amz-copy-source"].ToString())
            ? S3KeyHeaderFlag.CopySource
            : S3KeyHeaderFlag.None;

        return table.TryGetValue(new S3KeyRoute(method, sub, headerFlag), out var entry) ? Invoke(entry, bucket, key, ctx)
            : sub is not S3KeySubresource.None && table.TryGetValue(new S3KeyRoute(method, S3KeySubresource.None, headerFlag), out var fallback) ? Invoke(fallback, bucket, key, ctx)
            : Task.FromResult(http.Map(new MethodNotAllowedError($"{method} on key with subresource {sub} headerFlag {headerFlag}")));
    }

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
