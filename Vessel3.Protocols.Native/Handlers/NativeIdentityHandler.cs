using Microsoft.AspNetCore.Http;
using Vessel3.Primitives;
using Vessel3.Protocols.Native.Serialization;
using Vessel3.Storage;

namespace Vessel3.Protocols.Native.Handlers;

internal sealed class NativeIdentityHandler(IIdentityRegistry identity)
{
    public IResult WhoAmI(HttpContext context)
    {
        RequestTrace.SetTarget("WhoAmI");
        var caller = context.GetCaller();
        return caller is null
            ? new AccessDeniedError("Unauthorized").ToHttpResult()
            : Results.Json(
                new WhoAmIDto(caller.UserId, caller.Username, caller.Role.ToString(), caller.AccessKeyId),
                NativeJsonContext.Default.WhoAmIDto);
    }

    public IResult ListUsers(HttpContext context)
    {
        RequestTrace.SetTarget("ListUsers");
        var caller = context.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError("Admin role required").ToHttpResult();
        }

        var result = identity.ListUsers();
        if (!result.TryGetValue(out var users, out var err))
        {
            return err.ToHttpResult();
        }

        return Results.Json<List<UserDto>>([.. users.Select(user => new UserDto(user.Id, user.Username, user.Role.ToString(), user.Status.ToString(), user.CreatedAt))], NativeJsonContext.Default.ListUserDto);
    }

    public async Task<IResult> CreateUser(HttpContext context)
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
        if (!result.TryGetValue(out var user, out var err))
        {
            return err.ToHttpResult();
        }

        return Results.Json(new UserDto(user.Id, user.Username, user.Role.ToString(), user.Status.ToString(), user.CreatedAt), NativeJsonContext.Default.UserDto, statusCode: 201);
    }

    public IResult GetUser(string userId, HttpContext context)
    {
        RequestTrace.SetTarget("GetUser");
        var caller = context.GetCaller();
        if (caller is null || (!caller.IsAdmin && caller.UserId != userId))
        {
            return new AccessDeniedError("Forbidden").ToHttpResult();
        }

        var result = identity.GetUser(userId);
        if (!result.TryGetValue(out var user, out var err))
        {
            return err.ToHttpResult();
        }

        return Results.Json(new UserDto(user.Id, user.Username, user.Role.ToString(), user.Status.ToString(), user.CreatedAt), NativeJsonContext.Default.UserDto);
    }

    public IResult DeleteUser(string userId, HttpContext context)
    {
        RequestTrace.SetTarget("DeleteUser");
        var caller = context.GetCaller();
        if (caller is null || !caller.IsAdmin)
        {
            return new AccessDeniedError("Admin role required").ToHttpResult();
        }

        var result = identity.DeleteUser(userId);
        return result.TryGetError(out var err) ? err.ToHttpResult() : Results.NoContent();
    }

    public async Task<IResult> UpdateUserRole(string userId, HttpContext context)
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
        return result.TryGetError(out var err) ? err.ToHttpResult() : Results.NoContent();
    }

    public async Task<IResult> UpdateUserStatus(string userId, HttpContext context)
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
        return result.TryGetError(out var err) ? err.ToHttpResult() : Results.NoContent();
    }

    public IResult ListAccessKeys(string userId, HttpContext context)
    {
        RequestTrace.SetTarget("ListAccessKeys");
        var caller = context.GetCaller();
        if (caller is null || (!caller.IsAdmin && caller.UserId != userId))
        {
            return new AccessDeniedError("Forbidden").ToHttpResult();
        }

        var result = identity.ListAccessKeys(userId);
        if (!result.TryGetValue(out var keys, out var err))
        {
            return err.ToHttpResult();
        }

        return Results.Json<List<AccessKeyDto>>([.. keys.Select(key => new AccessKeyDto(key.Id, key.SecretKey, key.UserId, key.Description, key.CreatedAt, key.ExpiresAt, key.IsRevoked))], NativeJsonContext.Default.ListAccessKeyDto);
    }

    public async Task<IResult> CreateAccessKey(string userId, HttpContext context)
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
        if (!result.TryGetValue(out var key, out var err))
        {
            return err.ToHttpResult();
        }

        return Results.Json(new AccessKeyDto(key.Id, key.SecretKey, key.UserId, key.Description, key.CreatedAt, key.ExpiresAt, key.IsRevoked), NativeJsonContext.Default.AccessKeyDto, statusCode: 201);
    }

    public IResult RevokeAccessKey(string accessKeyId, HttpContext context)
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

        if (!caller.IsAdmin && key.UserId != caller.UserId)
        {
            return new AccessDeniedError("Forbidden").ToHttpResult();
        }

        var result = identity.RevokeAccessKey(accessKeyId);
        return result.TryGetError(out var revokeErr) ? revokeErr.ToHttpResult() : Results.NoContent();
    }
}
