using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;

namespace Vessel3.Protocols.Oci.Dispatch;

internal interface IOciDispatcher
{
    Task Dispatch(string path, HttpContext ctx);
}

internal sealed class OciDispatcher(IEnumerable<IOciAction> actions) : IOciDispatcher
{
    private readonly FrozenDictionary<OciOperationKind, IOciAction> actions = actions.ToFrozenDictionary(a => a.Operation);

    public async Task Dispatch(string path, HttpContext ctx)
    {
        ctx.Response.Headers.Append(OciHeaders.DockerDistributionApiVersion, OciHeaders.ApiVersionValue);
        var target = OciRequestParser.Parse(path, ctx);

        if (target.Operation is OciOperationKind.Unknown || !actions.TryGetValue(target.Operation, out var action))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var trace = RequestTrace.Current;
        if (trace is not null)
        {
            trace.Action = target.Operation.ToString();
            trace.Bucket = target.Repo;
            trace.Key = target.Reference ?? target.UploadId;
        }

        await action.Execute(target, ctx);
        trace?.MarkHandled();
    }
}
