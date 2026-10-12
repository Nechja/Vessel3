using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Protocols.Native.Adapters;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/iam/whoami", static (NativeIdentityAdapter adapter, HttpContext ctx) => adapter.WhoAmI(ctx));
        endpoints.MapGet("/v1/iam/users", static (NativeIdentityAdapter adapter, HttpContext ctx) => adapter.ListUsers(ctx));
        endpoints.MapPost("/v1/iam/users", static (NativeIdentityAdapter adapter, HttpContext ctx) => adapter.CreateUser(ctx));
        endpoints.MapGet("/v1/iam/users/{userId}", static (NativeIdentityAdapter adapter, string userId, HttpContext ctx) => adapter.GetUser(userId, ctx));
        endpoints.MapDelete("/v1/iam/users/{userId}", static (NativeIdentityAdapter adapter, string userId, HttpContext ctx) => adapter.DeleteUser(userId, ctx));
        endpoints.MapPut("/v1/iam/users/{userId}/role", static (NativeIdentityAdapter adapter, string userId, HttpContext ctx) => adapter.UpdateUserRole(userId, ctx));
        endpoints.MapPut("/v1/iam/users/{userId}/status", static (NativeIdentityAdapter adapter, string userId, HttpContext ctx) => adapter.UpdateUserStatus(userId, ctx));
        endpoints.MapGet("/v1/iam/users/{userId}/keys", static (NativeIdentityAdapter adapter, string userId, HttpContext ctx) => adapter.ListAccessKeys(userId, ctx));
        endpoints.MapPost("/v1/iam/users/{userId}/keys", static (NativeIdentityAdapter adapter, string userId, HttpContext ctx) => adapter.CreateAccessKey(userId, ctx));
        endpoints.MapDelete("/v1/iam/keys/{accessKeyId}", static (NativeIdentityAdapter adapter, string accessKeyId, HttpContext ctx) => adapter.RevokeAccessKey(accessKeyId, ctx));

        return endpoints;
    }
}
