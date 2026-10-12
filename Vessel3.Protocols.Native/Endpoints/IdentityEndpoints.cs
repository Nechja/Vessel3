using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Protocols.Native.Handlers;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/iam/whoami", static (NativeIdentityHandler handler, HttpContext ctx) => handler.WhoAmI(ctx));
        endpoints.MapGet("/v1/iam/users", static (NativeIdentityHandler handler, HttpContext ctx) => handler.ListUsers(ctx));
        endpoints.MapPost("/v1/iam/users", static (NativeIdentityHandler handler, HttpContext ctx) => handler.CreateUser(ctx));
        endpoints.MapGet("/v1/iam/users/{userId}", static (NativeIdentityHandler handler, string userId, HttpContext ctx) => handler.GetUser(userId, ctx));
        endpoints.MapDelete("/v1/iam/users/{userId}", static (NativeIdentityHandler handler, string userId, HttpContext ctx) => handler.DeleteUser(userId, ctx));
        endpoints.MapPut("/v1/iam/users/{userId}/role", static (NativeIdentityHandler handler, string userId, HttpContext ctx) => handler.UpdateUserRole(userId, ctx));
        endpoints.MapPut("/v1/iam/users/{userId}/status", static (NativeIdentityHandler handler, string userId, HttpContext ctx) => handler.UpdateUserStatus(userId, ctx));
        endpoints.MapGet("/v1/iam/users/{userId}/keys", static (NativeIdentityHandler handler, string userId, HttpContext ctx) => handler.ListAccessKeys(userId, ctx));
        endpoints.MapPost("/v1/iam/users/{userId}/keys", static (NativeIdentityHandler handler, string userId, HttpContext ctx) => handler.CreateAccessKey(userId, ctx));
        endpoints.MapDelete("/v1/iam/keys/{accessKeyId}", static (NativeIdentityHandler handler, string accessKeyId, HttpContext ctx) => handler.RevokeAccessKey(accessKeyId, ctx));

        return endpoints;
    }
}
