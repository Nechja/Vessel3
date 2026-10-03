using Microsoft.AspNetCore.Http;
using Vessel3.Storage;

namespace Vessel3.Protocols.WebDav;

internal static class WebDavContextExtensions
{
    public static CallerIdentity GetCaller(this HttpContext ctx) =>
        ctx.Items.TryGetValue(WebDavItems.CallerIdentity, out var obj) && obj is CallerIdentity caller
            ? caller
            : CallerIdentity.System;

    public static void SetCaller(this HttpContext ctx, CallerIdentity caller) =>
        ctx.Items[WebDavItems.CallerIdentity] = caller;
}
