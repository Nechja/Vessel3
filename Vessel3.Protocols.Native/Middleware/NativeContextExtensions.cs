using Microsoft.AspNetCore.Http;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native;

internal static class NativeContextExtensions
{
    public static CallerIdentity? GetCallerIdentity(this HttpContext ctx) =>
        ctx.Items.TryGetValue("CallerIdentity", out var obj) && obj is CallerIdentity caller ? caller : null;

    public static void SetCallerIdentity(this HttpContext ctx, CallerIdentity caller) =>
        ctx.Items["CallerIdentity"] = caller;
}
