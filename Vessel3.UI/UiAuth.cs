using Amazon.Runtime;

namespace Vessel3.UI;

internal sealed record UiSession(string AccessKey, string Secret, string Token, DateTimeOffset Expires, string Subject, string IdToken);

internal sealed class UiAuth
{
    public UiAuth(UiConfig config) =>
        Credentials = config.Oidc is null && !string.IsNullOrEmpty(config.AccessKey)
            ? new BasicAWSCredentials(config.AccessKey, config.SecretKey)
            : new AnonymousAWSCredentials();

    public AWSCredentials Credentials { get; private set; }
    public UiSession? Session { get; private set; }
    public string? Error { get; set; }
    public bool Anonymous => Credentials is AnonymousAWSCredentials;

    public void SignIn(UiSession session)
    {
        Session = session;
        Credentials = new SessionAWSCredentials(session.AccessKey, session.Secret, session.Token);
    }
}
