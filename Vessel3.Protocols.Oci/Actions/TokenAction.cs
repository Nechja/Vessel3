using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vessel3.Protocols.Oci.Dispatch;
using Vessel3.Storage;

namespace Vessel3.Protocols.Oci.Actions;

internal sealed class TokenAction(
    IIdentityRegistry identity,
    IContainerRepoTokenService tokenService,
    ContainerRepoAuthOptions options) : IOciAction
{
    public OciOperationKind Operation => OciOperationKind.Token;

    public async Task Execute(OciRequestTarget target, HttpContext ctx)
    {
        var authHeader = ctx.Request.Headers.Authorization.ToString().Trim();
        var (userId, canWrite) = Authenticate(authHeader);

        if (userId is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var scope = ctx.Request.Query.TryGetValue("scope", out var sVal) && sVal.Count > 0 && sVal[0] is { Length: > 0 } s ? s : "";
        var grantedScopes = ResolveScopes(scope, canWrite);
        var ttl = TimeSpan.FromHours(1);
        var token = tokenService.CreateToken(userId, grantedScopes, ttl);
        var nowIso = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        var responseDto = new TokenResponseDto(
            Token: token,
            AccessToken: token,
            ExpiresIn: (int)ttl.TotalSeconds,
            IssuedAt: nowIso);

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = OciMediaTypes.Json;
        await JsonSerializer.SerializeAsync(ctx.Response.Body, responseDto, OciJsonContext.Default.TokenResponseDto, ctx.RequestAborted);
    }

    private (string? UserId, bool CanWrite) Authenticate(string authHeader)
    {
        if (authHeader.StartsWith(OciHeaders.BasicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var basicStr = authHeader[OciHeaders.BasicPrefix.Length..].Trim();
            return TryDecodeBasic(basicStr, out var key, out var secret)
                ? AuthenticateCredentials(key, secret)
                : (null, false);
        }

        return options.IsUnauthenticated ? ("anonymous", true) : (null, false);
    }

    private (string? UserId, bool CanWrite) AuthenticateCredentials(string key, string secret)
    {
        if (options.RootAccessKey is not null && options.RootSecretKey is not null &&
            string.Equals(key, options.RootAccessKey, StringComparison.Ordinal) &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(options.RootSecretKey), Encoding.UTF8.GetBytes(secret)))
        {
            return ("admin", true);
        }

        if (identity.GetAccessKey(key).TryGetValue(out var keyEntry, out _) && keyEntry is not null &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(keyEntry.SecretKey), Encoding.UTF8.GetBytes(secret)) &&
            identity.AuthenticateAccessKey(key).TryGetValue(out var caller, out _) && caller is not null)
        {
            return (caller.UserId, caller.CanWrite);
        }

        return (null, false);
    }

    private static List<string> ResolveScopes(string scope, bool canWrite)
    {
        List<string> granted = [];
        if (string.IsNullOrEmpty(scope)) return granted;

        var parts = scope.Split(':');
        if (parts.Length < 3 || parts[0] != "repository") return granted;

        var repoName = parts[1];
        List<string> allowedActions = [.. parts[2]
            .Split(',')
            .Where(a => a == "pull" || (a == "push" && canWrite) || (a == "*" && canWrite))];

        if (allowedActions.Count > 0)
        {
            granted.Add($"repository:{repoName}:{string.Join(",", allowedActions)}");
        }

        return granted;
    }

    private static bool TryDecodeBasic(string basicStr, out string key, out string secret)
    {
        key = "";
        secret = "";
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(basicStr));
            var idx = decoded.IndexOf(':');
            if (idx <= 0) return false;
            key = decoded[..idx];
            secret = decoded[(idx + 1)..];
            return true;
        }
        catch
        {
            return false;
        }
    }
}
