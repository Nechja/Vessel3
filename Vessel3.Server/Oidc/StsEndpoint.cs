using System.Globalization;
using System.Xml;
using Vessel3.Server.S3;

namespace Vessel3.Server.Oidc;

internal static class StsEndpoint
{
    private const string Namespace = "https://sts.amazonaws.com/doc/2011-06-15/";
    private const string AssumeRoleWithWebIdentity = "AssumeRoleWithWebIdentity";
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(12);
    private static readonly XmlWriterSettings Settings = new() { Async = true, OmitXmlDeclaration = false, Encoding = new System.Text.UTF8Encoding(false) };

    public static bool Matches(HttpRequest req) =>
        HttpMethods.IsPost(req.Method) && req.Path == "/";

    public static async Task Handle(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        var form = ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync(ct) : null;
        string Param(string name) => form?[name].ToString() is { Length: > 0 } f ? f : ctx.Request.Query[name].ToString();

        if (Param("Action") != AssumeRoleWithWebIdentity)
        {
            await WriteError(ctx, 400, "InvalidAction", "Only AssumeRoleWithWebIdentity is supported", ct);
            return;
        }

        var token = Param("WebIdentityToken");
        if (token.Length is 0)
        {
            await WriteError(ctx, 400, "ValidationError", "WebIdentityToken is required", ct);
            return;
        }

        var duration = DefaultDuration;
        var rawDuration = Param("DurationSeconds");
        if (rawDuration.Length > 0)
        {
            if (!int.TryParse(rawDuration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                || seconds < MinDuration.TotalSeconds || seconds > MaxDuration.TotalSeconds)
            {
                await WriteError(ctx, 400, "ValidationError", $"DurationSeconds must be between {MinDuration.TotalSeconds:0} and {MaxDuration.TotalSeconds:0}", ct);
                return;
            }
            duration = TimeSpan.FromSeconds(seconds);
        }

        var verifier = ctx.RequestServices.GetRequiredService<ITokenVerifier>();
        if (!(await verifier.Verify(token, ct)).TryGetValue(out var identity, out var err))
        {
            await WriteError(ctx, err.Status, err.Code, err.Message, ct);
            return;
        }

        var session = ctx.RequestServices.GetRequiredService<ICredentialStore>().IssueSession(identity.Subject, duration);
        await WriteCredentials(ctx, identity, session, ct);
    }

    private static async Task WriteCredentials(HttpContext ctx, VerifiedIdentity identity, Credential session, CancellationToken ct)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/xml";
        await using var w = XmlWriter.Create(ctx.Response.Body, Settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "AssumeRoleWithWebIdentityResponse", Namespace);
        await w.WriteStartElementAsync(null, "AssumeRoleWithWebIdentityResult", null);
        await w.WriteElementStringAsync(null, "SubjectFromWebIdentityToken", null, identity.Subject);
        await w.WriteStartElementAsync(null, "Audiences", null);
        foreach (var aud in identity.Audiences)
            await w.WriteElementStringAsync(null, "member", null, aud);
        await w.WriteEndElementAsync();
        await w.WriteStartElementAsync(null, "AssumedRoleUser", null);
        await w.WriteElementStringAsync(null, "Arn", null, $"arn:aws:sts:::assumed-role/vessel3/{identity.Subject}");
        await w.WriteElementStringAsync(null, "AssumedRoleId", null, $"vessel3:{identity.Subject}");
        await w.WriteEndElementAsync();
        await w.WriteStartElementAsync(null, "Credentials", null);
        await w.WriteElementStringAsync(null, "AccessKeyId", null, session.AccessKey);
        await w.WriteElementStringAsync(null, "SecretAccessKey", null, session.Secret);
        await w.WriteElementStringAsync(null, "SessionToken", null, session.SessionToken!);
        await w.WriteElementStringAsync(null, "Expiration", null, session.ExpiresAt!.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteStartElementAsync(null, "ResponseMetadata", null);
        await w.WriteElementStringAsync(null, "RequestId", null, Guid.NewGuid().ToString("N"));
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }

    private static async Task WriteError(HttpContext ctx, int status, string code, string message, CancellationToken ct)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/xml";
        await using var w = XmlWriter.Create(ctx.Response.Body, Settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync(null, "ErrorResponse", Namespace);
        await w.WriteStartElementAsync(null, "Error", null);
        await w.WriteElementStringAsync(null, "Type", null, "Sender");
        await w.WriteElementStringAsync(null, "Code", null, code);
        await w.WriteElementStringAsync(null, "Message", null, message);
        await w.WriteEndElementAsync();
        await w.WriteElementStringAsync(null, "RequestId", null, Guid.NewGuid().ToString("N"));
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
    }
}
