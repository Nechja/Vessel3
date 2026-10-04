using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vessel3.Primitives;
using Vessel3.Protocols.Native.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native.Endpoints;

internal static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/iam/whoami", WhoAmI);
        endpoints.MapGet("/v1/iam/users", ListUsers);
        endpoints.MapPost("/v1/iam/users", CreateUser);
        endpoints.MapGet("/v1/iam/users/{userId}", GetUser);
        endpoints.MapDelete("/v1/iam/users/{userId}", DeleteUser);
        endpoints.MapPut("/v1/iam/users/{userId}/role", UpdateUserRole);
        endpoints.MapPut("/v1/iam/users/{userId}/status", UpdateUserStatus);
        endpoints.MapGet("/v1/iam/users/{userId}/keys", ListAccessKeys);
        endpoints.MapPost("/v1/iam/users/{userId}/keys", CreateAccessKey);
        endpoints.MapDelete("/v1/iam/keys/{accessKeyId}", RevokeAccessKey);

        return endpoints;
    }

    private static IResult WhoAmI(HttpContext context)
    {
        RequestTrace.SetTarget("WhoAmI");
        var caller = context.GetCaller();
        return caller is null
            ? new AccessDeniedError("Unauthorized").ToHttpResult()
            : Results.Json(
                new WhoAmIDto(caller.UserId, caller.Username, caller.Role.ToString(), caller.AccessKeyId),
                NativeJsonContext.Default.WhoAmIDto);
    }

    private static IResult ListUsers(HttpContext context, IIdentityRegistry identity)
    {
        RequestTrace.SetTarget("ListUsers");
        var caller = context.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError("Admin role required").ToHttpResult();
        }

        var result = identity.ListUsers();
        return result.Match(
            users => Results.Json<List<UserDto>>([.. users.Select(user => new UserDto(user.Id, user.Username, user.Role.ToString(), user.Status.ToString(), user.CreatedAt))], NativeJsonContext.Default.ListUserDto),
            NativeHttpResult.ToHttpResult);
    }

    private static async Task<IResult> CreateUser(HttpContext context, IIdentityRegistry identity)
    {
        RequestTrace.SetTarget("CreateUser");
        var caller = context.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError("Admin role required").ToHttpResult();
        }

        var body = await context.Request.ReadFromJsonAsync(NativeJsonContext.Default.CreateUserRequest);
        if (string.IsNullOrWhiteSpace(body.Username))
        {
            return new InvalidArgumentError("Username is required").ToHttpResult();
        }

        var role = Enum.TryParse<UserRole>(body.Role, ignoreCase: true, out var parsedRole) ? parsedRole : UserRole.Member;
        var result = identity.CreateUser(body.Username, role);
        return result.Match(
            user => Results.Json(new UserDto(user.Id, user.Username, user.Role.ToString(), user.Status.ToString(), user.CreatedAt), NativeJsonContext.Default.UserDto, statusCode: 201),
            NativeHttpResult.ToHttpResult);
    }

    private static IResult GetUser(string userId, HttpContext context, IIdentityRegistry identity)
    {
        RequestTrace.SetTarget("GetUser");
        var caller = context.GetCaller();
        if (caller is null || (!caller.IsAdmin && caller.UserId != userId))
        {
            return new AccessDeniedError("Forbidden").ToHttpResult();
        }

        var result = identity.GetUser(userId);
        return result.Match(
            user => user is not null
                ? Results.Json(new UserDto(user.Id, user.Username, user.Role.ToString(), user.Status.ToString(), user.CreatedAt), NativeJsonContext.Default.UserDto)
                : new NotFoundError($"User {userId}").ToHttpResult(),
            NativeHttpResult.ToHttpResult);
    }

    private static IResult DeleteUser(string userId, HttpContext context, IIdentityRegistry identity)
    {
        RequestTrace.SetTarget("DeleteUser");
        var caller = context.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError("Admin role required").ToHttpResult();
        }

        var result = identity.DeleteUser(userId);
        return result.Match(() => Results.NoContent(), NativeHttpResult.ToHttpResult);
    }

    private static async Task<IResult> UpdateUserRole(string userId, HttpContext context, IIdentityRegistry identity)
    {
        RequestTrace.SetTarget("UpdateUserRole");
        var caller = context.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError("Admin role required").ToHttpResult();
        }

        var body = await context.Request.ReadFromJsonAsync(NativeJsonContext.Default.UpdateUserRoleRequest);
        if (string.IsNullOrWhiteSpace(body.Role) || !Enum.TryParse<UserRole>(body.Role, ignoreCase: true, out var role))
        {
            return new InvalidArgumentError("Invalid role. Must be Admin, Member, or ReadOnly").ToHttpResult();
        }

        var result = identity.UpdateUserRole(userId, role);
        return result.Match(() => Results.NoContent(), NativeHttpResult.ToHttpResult);
    }

    private static async Task<IResult> UpdateUserStatus(string userId, HttpContext context, IIdentityRegistry identity)
    {
        RequestTrace.SetTarget("UpdateUserStatus");
        var caller = context.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError("Admin role required").ToHttpResult();
        }

        var body = await context.Request.ReadFromJsonAsync(NativeJsonContext.Default.UpdateUserStatusRequest);
        if (string.IsNullOrWhiteSpace(body.Status) || !Enum.TryParse<UserStatus>(body.Status, ignoreCase: true, out var status))
        {
            return new InvalidArgumentError("Invalid status. Must be Active or Suspended").ToHttpResult();
        }

        var result = identity.UpdateUserStatus(userId, status);
        return result.Match(() => Results.NoContent(), NativeHttpResult.ToHttpResult);
    }

    private static IResult ListAccessKeys(string userId, HttpContext context, IIdentityRegistry identity)
    {
        RequestTrace.SetTarget("ListAccessKeys");
        var caller = context.GetCaller();
        if (caller is null || (!caller.IsAdmin && caller.UserId != userId))
        {
            return new AccessDeniedError("Forbidden").ToHttpResult();
        }

        var result = identity.ListAccessKeys(userId);
        return result.Match(
            keys => Results.Json<List<AccessKeyDto>>([.. keys.Select(key => new AccessKeyDto(key.Id, key.SecretKey, key.UserId, key.Description, key.CreatedAt, key.ExpiresAt, key.IsRevoked))], NativeJsonContext.Default.ListAccessKeyDto),
            NativeHttpResult.ToHttpResult);
    }

    private static async Task<IResult> CreateAccessKey(string userId, HttpContext context, IIdentityRegistry identity)
    {
        RequestTrace.SetTarget("CreateAccessKey");
        var caller = context.GetCaller();
        if (caller is null || (!caller.IsAdmin && caller.UserId != userId))
        {
            return new AccessDeniedError("Forbidden").ToHttpResult();
        }

        var body = await context.Request.ReadFromJsonAsync(NativeJsonContext.Default.CreateAccessKeyRequest);
        var ttl = body.TtlSeconds.HasValue ? TimeSpan.FromSeconds(body.TtlSeconds.Value) : (TimeSpan?)null;
        var result = identity.CreateAccessKey(userId, body.Description, ttl);
        return result.Match(
            key => Results.Json(new AccessKeyDto(key.Id, key.SecretKey, key.UserId, key.Description, key.CreatedAt, key.ExpiresAt, key.IsRevoked), NativeJsonContext.Default.AccessKeyDto, statusCode: 201),
            NativeHttpResult.ToHttpResult);
    }

    private static IResult RevokeAccessKey(string accessKeyId, HttpContext context, IIdentityRegistry identity)
    {
        RequestTrace.SetTarget("RevokeAccessKey");
        var caller = context.GetCaller();
        if (caller is null)
        {
            return new AccessDeniedError("Unauthorized").ToHttpResult();
        }

        var keyResult = identity.GetAccessKey(accessKeyId);
        if (!keyResult.TryGetValue(out var key, out var error))
        {
            return error.ToHttpResult();
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
    }
}
