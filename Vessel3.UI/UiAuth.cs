using Vessel3.Client;

namespace Vessel3.UI;

internal sealed record UiSession(string? BearerToken, string? AccessKey, string? SecretKey, string Subject, DateTimeOffset? Expires);

internal sealed class UiAuth
{
    private readonly UiConfig config;

    public UiAuth(UiConfig config)
    {
        this.config = config;
        if (config.Oidc is null && !string.IsNullOrEmpty(config.AccessKey))
        {
            AccessKey = config.AccessKey;
            SecretKey = config.SecretKey;
        }
    }

    public string? BearerToken { get; private set; }
    public string? AccessKey { get; private set; }
    public string? SecretKey { get; private set; }
    public UiSession? Session { get; private set; }
    public WhoAmIDto? Caller { get; set; }
    public string? Error { get; set; }
    public bool SignedOut { get; set; }

    public bool IsOidc => config.Oidc is not null;
    public bool IsAnonymous => string.IsNullOrEmpty(BearerToken) && string.IsNullOrEmpty(AccessKey);

    public void SignInBearer(string token, string subject, DateTimeOffset? expires = null)
    {
        BearerToken = token;
        AccessKey = null;
        SecretKey = null;
        Session = new UiSession(token, null, null, subject, expires);
        SignedOut = false;
        Error = null;
    }

    public void SignInApiKey(string accessKey, string secretKey)
    {
        BearerToken = null;
        AccessKey = accessKey;
        SecretKey = secretKey;
        Session = new UiSession(null, accessKey, secretKey, accessKey, null);
        SignedOut = false;
        Error = null;
    }

    public void Clear()
    {
        BearerToken = null;
        AccessKey = null;
        SecretKey = null;
        Session = null;
        Caller = null;
        SignedOut = true;
    }
}
