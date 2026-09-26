using System.Globalization;
using Vessel3.Server.S3;

namespace Vessel3.Server.Oidc;

internal interface ISecurityTokenService
{
    bool Matches(HttpRequest req);
    Task Handle(HttpContext ctx);
}

internal sealed class SecurityTokenService(
    ITokenVerifier verifier,
    ICredentialStore credentials,
    ISecurityTokenXmlWriter xmlWriter) : ISecurityTokenService
{
    private const string AssumeRoleWithWebIdentity = "AssumeRoleWithWebIdentity";
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(12);

    public bool Matches(HttpRequest req) =>
        HttpMethods.IsPost(req.Method) && req.Path == "/";

    public async Task Handle(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync(ct) : null;
        string Param(string name) => form?[name].ToString() is { Length: > 0 } f ? f : ctx.Request.Query[name].ToString();

        if (Param("Action") != AssumeRoleWithWebIdentity)
        {
            await RespondError(ctx, 400, "InvalidAction", "Only AssumeRoleWithWebIdentity is supported", ct);
            return;
        }

        var token = Param("WebIdentityToken");
        if (token.Length is 0)
        {
            await RespondError(ctx, 400, "ValidationError", "WebIdentityToken is required", ct);
            return;
        }

        var duration = DefaultDuration;
        var rawDuration = Param("DurationSeconds");
        if (rawDuration.Length > 0)
        {
            if (!int.TryParse(rawDuration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                || seconds < MinDuration.TotalSeconds || seconds > MaxDuration.TotalSeconds)
            {
                await RespondError(ctx, 400, "ValidationError", $"DurationSeconds must be between {MinDuration.TotalSeconds:0} and {MaxDuration.TotalSeconds:0}", ct);
                return;
            }
            duration = TimeSpan.FromSeconds(seconds);
        }

        if (!(await verifier.Verify(token, ct)).TryGetValue(out var identity, out var err))
        {
            await RespondError(ctx, err.Status, err.Code, err.Message, ct);
            return;
        }

        var roleArn = Param("RoleArn");
        var roleSessionName = Param("RoleSessionName");
        var (accountId, roleName) = ParseRoleArn(roleArn);

        var session = credentials.IssueSession(identity.Subject, duration, accountId);
        var assumedSession = new AssumedRoleSession(
            identity.Subject,
            identity.Audiences,
            session.AccessKey,
            session.Secret,
            session.SessionToken!,
            session.ExpiresAt!.Value,
            accountId,
            roleName,
            roleSessionName.Length > 0 ? roleSessionName : null);

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/xml";
        await xmlWriter.WriteAssumeRoleResponse(ctx.Response.Body, assumedSession, ct);
    }

    private static (string? AccountId, string RoleName) ParseRoleArn(string roleArn)
    {
        if (string.IsNullOrEmpty(roleArn) || !roleArn.StartsWith("arn:aws:", StringComparison.Ordinal))
            return (null, "vessel3");

        var parts = roleArn.Split(':');
        var accountId = parts.Length >= 5 && parts[4].Length > 0 ? parts[4] : null;
        var roleName = parts.Length >= 6 && parts[5].StartsWith("role/", StringComparison.Ordinal)
            ? parts[5]["role/".Length..]
            : "vessel3";
        return (accountId, roleName);
    }

    private async Task RespondError(HttpContext ctx, int status, string code, string message, CancellationToken ct)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/xml";
        await xmlWriter.WriteErrorResponse(ctx.Response.Body, code, message, ct);
    }
}
