using System.Collections.Frozen;

namespace Vessel3.Server.S3;

internal interface IS3BucketActionDispatcher
{
    Task<IResult> Dispatch(string method, string bucket, HttpContext ctx);
}

internal sealed class S3BucketActionDispatcher(IEnumerable<IS3BucketAction> actions, IHttpResultMapper http) : IS3BucketActionDispatcher
{
    private readonly FrozenDictionary<S3BucketRoute, IS3BucketAction> table = actions.ToFrozenDictionary(a => a.Route);

    public Task<IResult> Dispatch(string method, string bucket, HttpContext ctx)
    {
        var sub = S3BucketSubresourceParser.From(ctx.Request.Query);

        return table.TryGetValue(new S3BucketRoute(method, sub), out var action) ? action.Invoke(bucket, ctx)
            : sub is not S3BucketSubresource.None && table.TryGetValue(new S3BucketRoute(method, S3BucketSubresource.None), out var fallback) ? fallback.Invoke(bucket, ctx)
            : Task.FromResult(http.Map(new MethodNotAllowedError($"{method} on bucket with subresource {sub}")));
    }
}
