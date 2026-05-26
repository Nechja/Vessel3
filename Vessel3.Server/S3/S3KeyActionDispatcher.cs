using System.Collections.Frozen;

namespace Vessel3.Server.S3;

internal interface IS3KeyActionDispatcher
{
    Task<IResult> Dispatch(string method, string bucket, string key, HttpContext ctx);
}

internal sealed class S3KeyActionDispatcher(IEnumerable<IS3KeyAction> actions, IHttpResultMapper http) : IS3KeyActionDispatcher
{
    private readonly FrozenDictionary<S3KeyRoute, IS3KeyAction> table = actions.ToFrozenDictionary(a => a.Route);

    public Task<IResult> Dispatch(string method, string bucket, string key, HttpContext ctx)
    {
        var sub = S3KeySubresourceParser.From(ctx.Request.Query);

        return table.TryGetValue(new S3KeyRoute(method, sub), out var action) ? action.Invoke(bucket, key, ctx)
            : sub is not S3KeySubresource.None && table.TryGetValue(new S3KeyRoute(method, S3KeySubresource.None), out var fallback) ? fallback.Invoke(bucket, key, ctx)
            : Task.FromResult(http.Map(new MethodNotAllowedError($"{method} on key with subresource {sub}")));
    }
}
