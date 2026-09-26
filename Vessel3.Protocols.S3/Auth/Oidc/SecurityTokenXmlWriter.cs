using System.Globalization;
using System.Security;
using System.Text;

namespace Vessel3.Server.Oidc;

internal sealed record AssumedRoleSession(
    string Subject,
    IReadOnlyList<string> Audiences,
    string AccessKeyId,
    string SecretAccessKey,
    string SessionToken,
    DateTimeOffset Expiration,
    string? AccountId = null,
    string RoleName = "vessel3",
    string? RoleSessionName = null,
    string? RequestId = null);

internal interface ISecurityTokenXmlWriter
{
    Task WriteAssumeRoleResponse(Stream output, AssumedRoleSession session, CancellationToken ct);
    Task WriteErrorResponse(Stream output, string code, string message, CancellationToken ct);
}

internal sealed class SecurityTokenXmlWriter : ISecurityTokenXmlWriter
{
    private const string Namespace = "https://sts.amazonaws.com/doc/2011-06-15/";

    public Task WriteAssumeRoleResponse(Stream output, AssumedRoleSession session, CancellationToken ct)
    {
        var audiencesXml = string.Concat(session.Audiences.Select(aud => $"<member>{SecurityElement.Escape(aud)}</member>"));
        var exp = session.Expiration.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var subject = SecurityElement.Escape(session.Subject);
        var roleSessionName = SecurityElement.Escape(session.RoleSessionName ?? session.Subject);
        var accountId = session.AccountId ?? "";
        var roleName = SecurityElement.Escape(session.RoleName);
        var reqId = session.RequestId ?? Guid.NewGuid().ToString("N");

        var xml = $"""
            <AssumeRoleWithWebIdentityResponse xmlns="{Namespace}">
              <AssumeRoleWithWebIdentityResult>
                <SubjectFromWebIdentityToken>{subject}</SubjectFromWebIdentityToken>
                <Audiences>{audiencesXml}</Audiences>
                <AssumedRoleUser>
                  <Arn>arn:aws:sts::{accountId}:assumed-role/{roleName}/{roleSessionName}</Arn>
                  <AssumedRoleId>{roleName}:{roleSessionName}</AssumedRoleId>
                </AssumedRoleUser>
                <Credentials>
                  <AccessKeyId>{session.AccessKeyId}</AccessKeyId>
                  <SecretAccessKey>{session.SecretAccessKey}</SecretAccessKey>
                  <SessionToken>{session.SessionToken}</SessionToken>
                  <Expiration>{exp}</Expiration>
                </Credentials>
              </AssumeRoleWithWebIdentityResult>
              <ResponseMetadata>
                <RequestId>{reqId}</RequestId>
              </ResponseMetadata>
            </AssumeRoleWithWebIdentityResponse>
            """;

        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
    }

    public Task WriteErrorResponse(Stream output, string code, string message, CancellationToken ct)
    {
        var xml = $"""
            <ErrorResponse xmlns="{Namespace}">
              <Error>
                <Type>Sender</Type>
                <Code>{SecurityElement.Escape(code)}</Code>
                <Message>{SecurityElement.Escape(message)}</Message>
              </Error>
              <ResponseMetadata>
                <RequestId>{Guid.NewGuid():N}</RequestId>
              </ResponseMetadata>
            </ErrorResponse>
            """;

        return output.WriteAsync(Encoding.UTF8.GetBytes(xml), ct).AsTask();
    }
}
