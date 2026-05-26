using System.Collections.Frozen;

namespace Vessel3.Server.S3;

internal interface IS3ActionDispatcher
{
    Task<IResult> Dispatch(string method, string bucket, HttpContext ctx);
}

internal sealed class S3ActionDispatcher(IEnumerable<IS3Action> actions, IHttpResultMapper http) : IS3ActionDispatcher
{
    private readonly FrozenDictionary<S3Route, IS3Action> table = actions.ToFrozenDictionary(a => a.Route);

    public Task<IResult> Dispatch(string method, string bucket, HttpContext ctx)
    {
        var sub = S3SubresourceParser.From(ctx.Request.Query);

        return table.TryGetValue(new S3Route(method, sub), out var action) ? action.Invoke(bucket, ctx)
            : sub is not S3Subresource.None && table.TryGetValue(new S3Route(method, S3Subresource.None), out var fallback) ? fallback.Invoke(bucket, ctx)
            : Task.FromResult(http.Map(new MethodNotAllowedError($"{method} on bucket with subresource {sub}")));
    }
}
