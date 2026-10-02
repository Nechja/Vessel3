using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Primitives;
using Vessel3.Protocols.Native;
using Vessel3.Protocols.Native.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/iam/whoami", static (HttpContext ctx) =>
        {
            RequestTrace.SetTarget("WhoAmI");
            var caller = ctx.GetCaller();
            return caller is null
                ? new AccessDeniedError("Unauthorized").ToHttpResult()
                : Results.Json(
                    new WhoAmIDto(caller.UserId, caller.Username, caller.Role.ToString(), caller.AccessKeyId),
                    NativeJsonContext.Default.WhoAmIDto);
        });

        endpoints.MapGet("/v1/iam/users", static (HttpContext ctx, IIdentityRegistry identity) =>
        {
            RequestTrace.SetTarget("ListUsers");
            var caller = ctx.GetCaller();
            if (caller is null || !caller.IsAdmin)
            {
                return new AccessDeniedError("Admin role required").ToHttpResult();
            }

            var result = identity.ListUsers();
            return result.Match(
                users => Results.Json<List<UserDto>>([.. users.Select(u => new UserDto(u.Id, u.Username, u.Role.ToString(), u.Status.ToString(), u.CreatedAt))], NativeJsonContext.Default.ListUserDto),
                NativeHttpResult.ToHttpResult);
        });

        endpoints.MapPost("/v1/iam/users", static async (HttpContext ctx, IIdentityRegistry identity) =>
        {
            RequestTrace.SetTarget("CreateUser");
            var caller = ctx.GetCaller();
            if (caller is null || !caller.IsAdmin)
            {
                return new AccessDeniedError("Admin role required").ToHttpResult();
            }

            var body = await ctx.Request.ReadFromJsonAsync(NativeJsonContext.Default.CreateUserRequest);
            if (string.IsNullOrWhiteSpace(body.Username))
            {
                return new InvalidArgumentError("Username is required").ToHttpResult();
            }

            var role = Enum.TryParse<UserRole>(body.Role, ignoreCase: true, out var r) ? r : UserRole.Member;
            var result = identity.CreateUser(body.Username, role);
            return result.Match(
                u => Results.Json(new UserDto(u.Id, u.Username, u.Role.ToString(), u.Status.ToString(), u.CreatedAt), NativeJsonContext.Default.UserDto, statusCode: 201),
                NativeHttpResult.ToHttpResult);
        });

        endpoints.MapGet("/v1/iam/users/{userId}", static (string userId, HttpContext ctx, IIdentityRegistry identity) =>
        {
            RequestTrace.SetTarget("GetUser");
            var caller = ctx.GetCaller();
            if (caller is null || (!caller.IsAdmin && caller.UserId != userId))
            {
                return new AccessDeniedError("Forbidden").ToHttpResult();
            }

            var result = identity.GetUser(userId);
            return result.Match(
                u => u is not null
                    ? Results.Json(new UserDto(u.Id, u.Username, u.Role.ToString(), u.Status.ToString(), u.CreatedAt), NativeJsonContext.Default.UserDto)
                    : new NotFoundError($"User {userId}").ToHttpResult(),
                NativeHttpResult.ToHttpResult);
        });

        endpoints.MapDelete("/v1/iam/users/{userId}", static (string userId, HttpContext ctx, IIdentityRegistry identity) =>
        {
            RequestTrace.SetTarget("DeleteUser");
            var caller = ctx.GetCaller();
            if (caller is null || !caller.IsAdmin)
            {
                return new AccessDeniedError("Admin role required").ToHttpResult();
            }

            var result = identity.DeleteUser(userId);
            return result.Match(() => Results.NoContent(), NativeHttpResult.ToHttpResult);
        });

        endpoints.MapPut("/v1/iam/users/{userId}/role", static async (string userId, HttpContext ctx, IIdentityRegistry identity) =>
        {
            RequestTrace.SetTarget("UpdateUserRole");
            var caller = ctx.GetCaller();
            if (caller is null || !caller.IsAdmin)
            {
                return new AccessDeniedError("Admin role required").ToHttpResult();
            }

            var body = await ctx.Request.ReadFromJsonAsync(NativeJsonContext.Default.UpdateUserRoleRequest);
            if (string.IsNullOrWhiteSpace(body.Role) || !Enum.TryParse<UserRole>(body.Role, ignoreCase: true, out var role))
            {
                return new InvalidArgumentError("Invalid role. Must be Admin, Member, or ReadOnly").ToHttpResult();
            }

            var result = identity.UpdateUserRole(userId, role);
            return result.Match(() => Results.NoContent(), NativeHttpResult.ToHttpResult);
        });

        endpoints.MapPut("/v1/iam/users/{userId}/status", static async (string userId, HttpContext ctx, IIdentityRegistry identity) =>
        {
            RequestTrace.SetTarget("UpdateUserStatus");
            var caller = ctx.GetCaller();
            if (caller is null || !caller.IsAdmin)
            {
                return new AccessDeniedError("Admin role required").ToHttpResult();
            }

            var body = await ctx.Request.ReadFromJsonAsync(NativeJsonContext.Default.UpdateUserStatusRequest);
            if (string.IsNullOrWhiteSpace(body.Status) || !Enum.TryParse<UserStatus>(body.Status, ignoreCase: true, out var status))
            {
                return new InvalidArgumentError("Invalid status. Must be Active or Suspended").ToHttpResult();
            }

            var result = identity.UpdateUserStatus(userId, status);
            return result.Match(() => Results.NoContent(), NativeHttpResult.ToHttpResult);
        });

        endpoints.MapGet("/v1/iam/users/{userId}/keys", static (string userId, HttpContext ctx, IIdentityRegistry identity) =>
        {
            RequestTrace.SetTarget("ListAccessKeys");
            var caller = ctx.GetCaller();
            if (caller is null || (!caller.IsAdmin && caller.UserId != userId))
            {
                return new AccessDeniedError("Forbidden").ToHttpResult();
            }

            var result = identity.ListAccessKeys(userId);
            return result.Match(
                keys => Results.Json<List<AccessKeyDto>>([.. keys.Select(k => new AccessKeyDto(k.Id, k.SecretKey, k.UserId, k.Description, k.CreatedAt, k.ExpiresAt, k.IsRevoked))], NativeJsonContext.Default.ListAccessKeyDto),
                NativeHttpResult.ToHttpResult);
        });

        endpoints.MapPost("/v1/iam/users/{userId}/keys", static async (string userId, HttpContext ctx, IIdentityRegistry identity) =>
        {
            RequestTrace.SetTarget("CreateAccessKey");
            var caller = ctx.GetCaller();
            if (caller is null || (!caller.IsAdmin && caller.UserId != userId))
            {
                return new AccessDeniedError("Forbidden").ToHttpResult();
            }

            var body = await ctx.Request.ReadFromJsonAsync(NativeJsonContext.Default.CreateAccessKeyRequest);
            var ttl = body.TtlSeconds.HasValue ? TimeSpan.FromSeconds(body.TtlSeconds.Value) : (TimeSpan?)null;
            var result = identity.CreateAccessKey(userId, body.Description, ttl);
            return result.Match(
                k => Results.Json(new AccessKeyDto(k.Id, k.SecretKey, k.UserId, k.Description, k.CreatedAt, k.ExpiresAt, k.IsRevoked), NativeJsonContext.Default.AccessKeyDto, statusCode: 201),
                NativeHttpResult.ToHttpResult);
        });

        endpoints.MapDelete("/v1/iam/keys/{accessKeyId}", static (string accessKeyId, HttpContext ctx, IIdentityRegistry identity) =>
        {
            RequestTrace.SetTarget("RevokeAccessKey");
            var caller = ctx.GetCaller();
            if (caller is null)
            {
                return new AccessDeniedError("Unauthorized").ToHttpResult();
            }

            var keyResult = identity.GetAccessKey(accessKeyId);
            if (!keyResult.TryGetValue(out var key, out var err))
            {
                return err.ToHttpResult();
            }

            if (key is null)
            {
                return new NotFoundError($"AccessKey {accessKeyId}").ToHttpResult();
            }

            if (!caller.IsAdmin && key.UserId != caller.UserId)
            {
                return new AccessDeniedError("Forbidden").ToHttpResult();
            }

            var result = identity.RevokeAccessKey(accessKeyId);
            return result.Match(() => Results.NoContent(), NativeHttpResult.ToHttpResult);
        });

        return endpoints;
    }
}
